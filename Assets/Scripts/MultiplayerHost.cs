// Imports delegates, string comparisons, and exception types.
using System;
// Imports Dictionary for mapping network clients to table seats.
using System.Collections.Generic;
// Imports IPAddress and IPEndPoint for reading and validating IPv4 addresses.
using System.Net;
// Imports the socket used to discover the local route. Gameplay uses Unity Transport.
using System.Net.Sockets;
// Imports Allocator.Temp for short-lived network buffers.
using Unity.Collections;
// Imports Netcode's connection, messaging and buffer types.
using Unity.Netcode;
// Imports Unity Transport, which carries LAN messages over UDP.
using Unity.Netcode.Transports.UTP;
// Imports Unity components and GameObjects.
using UnityEngine;

// Attaches the LAN adapter to the game. TableUI calls BlackjackGame, which calls this adapter.
public class MultiplayerHost : MonoBehaviour
{
    // UDP port shared by Host and Join. Change it on both builds and allow the new port through the firewall.
    public const ushort PORT = 7777;
    // Base-36 digit order for join codes. Changing the order makes codes incompatible with older builds.
    const string CHARS = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    // Wire label for guest actions. Sender and receiver must use the same label.
    const string CommandMessage = "Blackjack.Command.v1";
    // Wire label for the host's seat assignment. A mismatch prevents finishing Join.
    const string SeatMessage = "Blackjack.Seat.v1";
    // Wire label for table snapshots. A mismatch prevents guests from seeing updates.
    const string StateMessage = "Blackjack.State.v1";
    // Maximum serialized snapshot size in bytes. Lowering it can make valid snapshots fail; increasing it reserves more temporary memory.
    const int StateCapacity = 4096;

    // True when hosting or when a guest has received a valid seat.
    public bool isConnected { get; private set; }
    // True on the device running the authoritative game.
    public bool isHost { get; private set; }
    // True while a guest waits for the host to assign a seat.
    public bool IsJoining { get; private set; }
    // Address or encoded IPv4 text the host can share; it is not a password.
    public string joinCode { get; private set; } = "";
    // Status text read by the table UI.
    public string ConnectionMessage { get; private set; } = "";

    // BlackjackGame supplies the next free guest seat or a negative result when full.
    public Func<int> OnAssignSeat;
    // Hands BlackjackGame the host-assigned seat and a command such as HIT.
    public Action<int, string> OnClientCommand;
    // Tells BlackjackGame which guest seat to release.
    public Action<int> OnClientLeft;
    // Tells the guest game which seat belongs to this device.
    public Action<int> OnJoinedAsClient;
    // Passes received JSON to the guest game for validation and display.
    public Action<string> OnStateReceived;
    // Asks the game to refresh after a connection status change.
    public Action OnConnectionChanged;
    // Resets the table when an established session ends.
    public Action OnHostLost;

    // Owns the Netcode session and connection callbacks.
    NetworkManager manager;
    // Sends and receives packets for the manager.
    UnityTransport transport;
    // Stores the reason before Netcode clears its connection data.
    string disconnectReason;
    // Maps connection IDs assigned by Netcode to seat numbers assigned by the game.
    readonly Dictionary<ulong, int> seats = new Dictionary<ulong, int>();

    // Creates networking components on the first Host or Join call.
    void EnsureManager()
    {
        // Reuses the manager on subsequent attempts; creating another can conflict with Netcode's singleton.
        if (manager != null) return;
        // Creates the root GameObject that owns both networking components. Its name is for the hierarchy.
        var root = new GameObject("Blackjack LAN Network");
        // Adds the transport before configuring Netcode.
        transport = root.AddComponent<UnityTransport>();
        // Adds Netcode's session controller to the same object.
        manager = root.AddComponent<NetworkManager>();
        // Replaces the default session settings with the settings below.
        manager.NetworkConfig = new NetworkConfig
        {
            // Connects the manager to the transport created above.
            NetworkTransport = transport,
            // Both devices already have the table scene. True enables Netcode's scene synchronization workflow.
            EnableSceneManagement = false,
            // True lets the host enforce seat capacity and reserve seats before admitting guests.
            ConnectionApproval = true,
            // Seconds Netcode allows for connection approval. Lower values fail stalled attempts sooner.
            ClientConnectionBufferTimeout = 8,
            // Connection compatibility number. Change it on both devices when intentionally breaking protocol compatibility.
            ProtocolVersion = 1
        };
        // Registers the method that fills out Netcode's approval response.
        manager.ConnectionApprovalCallback = Approve;
        // Registers the handler for completed connections.
        manager.OnClientConnectedCallback += ClientConnected;
        // Registers the handler that frees seats and captures failure reasons.
        manager.OnClientDisconnectCallback += ClientDisconnected;
        // Registers the handler that resets local session state.
        manager.OnClientStopped += ClientStopped;
    }

    // Unity calls this as the adapter is destroyed.
    void OnDestroy()
    {
        // There is nothing to clean up if Host and Join were never used.
        if (manager == null) return;
        // Detaches the connected handler before shutdown.
        manager.OnClientConnectedCallback -= ClientConnected;
        // Detaches the disconnected handler before shutdown.
        manager.OnClientDisconnectCallback -= ClientDisconnected;
        // Prevents shutdown from calling the table reset while the object is being destroyed.
        manager.OnClientStopped -= ClientStopped;
        // Stops Netcode. True discards queued messages during teardown.
        manager.Shutdown(true);
        // Destroys the network root and its components.
        Destroy(manager.gameObject);
    }

    // Updates the text shown to the player. The parameter changes the message only.
    void Report(string message)
    {
        // Stores the current status so the UI can read it during refresh.
        ConnectionMessage = message;
        // Calls the refresh callback if one has been assigned.
        OnConnectionChanged?.Invoke();
    }

    // Direct IP setup follows Bitesize ClientDriven/HostJoinUI.cs.
    // Starts a host session and returns a shareable address or code. Empty means hosting did not start.
    public string StartHost()
    {
        // Creates the networking components if needed.
        EnsureManager();
        // Prevents starting a second session while one is active or shutting down.
        if (manager.IsListening || manager.ShutdownInProgress) return "";
        // First argument is the local destination address, PORT is UDP, and 0.0.0.0 listens on all IPv4 interfaces. Binding only to 127.0.0.1 would prevent other devices joining.
        transport.SetConnectionData("127.0.0.1", PORT, "0.0.0.0");
        // Starts the server and its local client. On failure, the body reports the problem.
        if (!manager.StartHost())
        {
            // Shows a likely cause of a bind failure. Update the wording if you change PORT.
            Report("Could not host. Check whether UDP port 7777 is already in use.");
            // Returns no join code for a failed host attempt.
            return "";
        }

        // Routes incoming action messages to ReceiveCommand after Netcode starts.
        manager.CustomMessagingManager.RegisterNamedMessageHandler(CommandMessage, ReceiveCommand);
        // Marks the local game ready to host and accept player actions.
        isHost = isConnected = true;
        // Finds the LAN address and encodes it when supported.
        joinCode = IpToCode(GetLocalIp());
        // Shows the address or code to share with guests on the LAN.
        Report("Share code " + joinCode + " with players on your LAN.");
        // Returns the code to the Host button so it can populate the input field.
        return joinCode;
    }

    // Takes the user-entered code or IPv4 address and begins connecting as a guest.
    public void Join(string code)
    {
        // Creates or reuses the networking components.
        EnsureManager();
        // Ignores another Join click while a network session is busy.
        if (manager.IsListening || manager.ShutdownInProgress) return;
        // Decodes the input or validates it as a plain IPv4 address.
        string ip = CodeToIp(code);
        // Checks for invalid input before configuring transport.
        if (ip == null)
        {
            // Explains what input the Join field accepts.
            Report("Enter a valid join code or IPv4 address.");
            // Exits without starting a connection.
            return;
        }

        // Uses the host's IP as the destination and the same UDP port as StartHost.
        transport.SetConnectionData(ip, PORT);
        // Clears a previous attempt's error before starting a new one.
        disconnectReason = null;
        // Starts the connection attempt. A successful return means it started, not that a seat is assigned.
        if (!manager.StartClient())
        {
            // Displays failure to start the client.
            Report("Could not start the LAN connection.");
            // Exits without entering the joining state.
            return;
        }
        // Keeps the UI waiting until a seat message arrives.
        IsJoining = true;
        // Routes seat messages to ReceiveSeat.
        manager.CustomMessagingManager.RegisterNamedMessageHandler(SeatMessage, ReceiveSeat);
        // Routes snapshots to ReceiveState.
        manager.CustomMessagingManager.RegisterNamedMessageHandler(StateMessage, ReceiveState);
        // Refreshes the display while the connection is pending.
        Report("Connecting…");
    }

    // Netcode supplies the incoming request and a response object to fill in. The request identifies the client.
    void Approve(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        // False keeps the scene's existing table UI. True asks Netcode to spawn a player prefab.
        response.CreatePlayerObject = false;
        // NGO also approves the local host, which already owns seat zero.
        // Detects the local host using Netcode's reserved server client ID.
        if (request.ClientNetworkId == NetworkManager.ServerClientId)
        {
            // Lets the local host enter without using a guest seat.
            response.Approved = true;
            // Skips guest assignment for the host.
            return;
        }
        // Asks the game for a free seat; -1 is the fallback when no callback exists.
        int seat = OnAssignSeat?.Invoke() ?? -1;
        // Accepts guest seats 1–3. Increasing 3 here alone cannot add seats to the game or UI.
        response.Approved = seat >= 1 && seat <= 3;
        // Records which connection owns the seat so later actions can be attributed to it.
        if (response.Approved) seats.Add(request.ClientNetworkId, seat);
        // Supplies the rejection message sent to a guest when no seat is available.
        else response.Reason = "The table is full.";
    }

    // Receives a connection ID after Netcode completes a connection.
    void ClientConnected(ulong client)
    {
        // Only the host sends a seat, and only to IDs with a recorded assignment.
        if (!manager.IsServer || !seats.TryGetValue(client, out int seat)) return;
        // Allocates four bytes for one int. Allocator.Temp and using make this buffer short-lived.
        using var writer = new FastBufferWriter(sizeof(int), Allocator.Temp);
        // Writes the assigned seat number into the buffer.
        writer.WriteValueSafe(seat);
        // Sends the named message to that client. ReliableFragmentedSequenced delivers in order on the same delivery channel as snapshots.
        manager.CustomMessagingManager.SendNamedMessage(SeatMessage, client, writer, NetworkDelivery.ReliableFragmentedSequenced);
        // Tells the game to broadcast the initial table state after sending the seat.
        OnClientCommand?.Invoke(seat, "JOIN");
    }

    // Receives the connection ID that disconnected.
    void ClientDisconnected(ulong client)
    {
        // On the guest, capture the disconnect reason before shutdown clears it.
        if (!manager.IsServer)
            // Copies the most recent reason from Netcode.
            disconnectReason = manager.DisconnectReason;
        // On the host, uses the disconnected client's ID to retrieve its seat.
        if (manager.IsServer && seats.TryGetValue(client, out int seat))
        {
            // Removes the mapping so the departed client cannot act for that seat.
            seats.Remove(client);
            // Releases the seat during normal play. Full shutdown cleans up the whole session instead.
            if (!manager.ShutdownInProgress) OnClientLeft?.Invoke(seat);
        }
    }

    // Called when the local client stops. Netcode passes its former host role; this handler does not use that parameter.
    void ClientStopped(bool wasHost)
    {
        // Remembers whether the session had reached an assigned seat or active host.
        bool wasConnected = isConnected;
        // Starts with the stored network reason, if any.
        string reason = disconnectReason;
        // Drops all connection-to-seat assignments from the finished session.
        seats.Clear();
        // Clears the flags so a fresh session can begin.
        isConnected = isHost = IsJoining = false;
        // Resets the game only if the ended session was active.
        if (wasConnected) OnHostLost?.Invoke();
        // Uses a default status only when Netcode gave no reason.
        if (string.IsNullOrEmpty(reason))
            // Chooses distinct text for an ended session and a failed join.
            reason = wasConnected ? "Connection ended." : "Could not join. Check the host address and LAN connection.";
        // Displays the reason and refreshes the UI.
        Report(reason);
    }

    // Accepts a seat message. Sender is the connection ID; reader contains the message bytes.
    void ReceiveSeat(ulong sender, FastBufferReader reader)
    {
        // Accepts the assignment only while joining, from the host, with at least four bytes for the seat.
        if (!IsJoining || sender != NetworkManager.ServerClientId || !reader.TryBeginRead(sizeof(int))) return;
        // Reads a four-byte seat number. The host writer must use the same type.
        reader.ReadValue(out int seat);
        // Rejects numbers outside the guest seat range.
        if (seat < 1 || seat > 3) return;
        // Ends the wait for assignment.
        IsJoining = false;
        // Marks the guest ready for table snapshots.
        isConnected = true;
        // Passes the assigned seat to the local game so it knows whose turn counts as yours.
        OnJoinedAsClient?.Invoke(seat);
        // Clears the connecting message and refreshes the UI.
        Report("");
    }

    // Reads a guest action; sender identifies the connection and reader supplies its bytes.
    void ReceiveCommand(ulong sender, FastBufferReader reader)
    {
        // Only the host accepts actions. It looks up the seat and requires one byte in the message.
        if (!isHost || !seats.TryGetValue(sender, out int seat) || !reader.TryBeginRead(1)) return;
        // Reads the numeric action written by SendToHost.
        reader.ReadValue(out byte action);
        // Translates supported wire values into game commands. Unknown values are ignored.
        switch (action)
        {
            // Action 1 requests one card. Changing its number requires updating SendToHost too.
            case 1: OnClientCommand?.Invoke(seat, "HIT"); break;
            // Action 2 requests ending the turn. Keep both mappings in sync.
            case 2: OnClientCommand?.Invoke(seat, "STAND"); break;
            // Action 3 requests a lower bet; the game enforces the minimum.
            case 3: OnClientCommand?.Invoke(seat, "LESS"); break;
            // Action 4 requests a higher bet; the game enforces the bankroll limit.
            case 4: OnClientCommand?.Invoke(seat, "MORE"); break;
        }
    }

    // Receives a full table snapshot; sender identifies who sent it and reader holds the serialized bytes.
    void ReceiveState(ulong sender, FastBufferReader reader)
    {
        // Accepts a snapshot only on a seated guest, from the host, within the byte limit.
        if (!isConnected || isHost || sender != NetworkManager.ServerClientId || reader.Length > StateCapacity) return;
        // Attempts to decode and apply the state.
        try
        {
            // Reads the length-prefixed string with bounds checking.
            reader.ReadValueSafe(out string json);
            // Passes JSON to BlackjackGame, which validates the data and updates the table.
            OnStateReceived?.Invoke(json);
        }
        // Catches malformed values or an out-of-bounds payload.
        catch (Exception e) when (e is ArgumentException || e is OverflowException)
        {
            // Ends the guest session if it cannot safely apply the snapshot.
            manager.Shutdown();
        }
    }

    // Takes a full serialized table state from BlackjackGame and broadcasts it to assigned guests.
    public void SendToClients(string json)
    {
        // Only the running host can broadcast state.
        if (!isHost || manager.ShutdownInProgress) return;
        // Allocates a temporary buffer of StateCapacity bytes. A larger serialization cannot fit.
        using var writer = new FastBufferWriter(StateCapacity, Allocator.Temp);
        // Encodes the length and text using the same format ReceiveState reads.
        writer.WriteValueSafe(json);
        // Visits each currently connected client ID.
        foreach (ulong client in manager.ConnectedClientsIds)
        {
            // Sends only to assigned guests; the local host already has the state.
            if (seats.ContainsKey(client))
                // Queues the snapshot using reliable ordered delivery with fragmentation for larger payloads.
                manager.CustomMessagingManager.SendNamedMessage(StateMessage, client, writer, NetworkDelivery.ReliableFragmentedSequenced);
        }
    }

    // Takes HIT, STAND, LESS or MORE and requests that the host apply it to the sender's seat.
    public void SendToHost(string command)
    {
        // Requires a connected guest and a manager that is not shutting down.
        if (!isConnected || isHost || manager.ShutdownInProgress) return;
        // Stores the compact command ID that goes on the wire.
        byte action;
        // Translates the game's command names into the agreed byte values.
        switch (command)
        {
            // Encodes Hit as 1. Update ReceiveCommand if this changes.
            case "HIT": action = 1; break;
            // Encodes Stand as 2. Update ReceiveCommand if this changes.
            case "STAND": action = 2; break;
            // Encodes a bet decrease as 3.
            case "LESS": action = 3; break;
            // Encodes a bet increase as 4.
            case "MORE": action = 4; break;
            // Drops unrecognized strings without sending a packet.
            default: return;
        }
        // Reserves exactly one byte. A larger action type needs matching buffer and reader changes.
        using var writer = new FastBufferWriter(1, Allocator.Temp);
        // Writes the byte into the buffer.
        writer.WriteValueSafe(action);
        // Sends only to the host's reserved ID using Netcode's default reliable sequenced delivery.
        manager.CustomMessagingManager.SendNamedMessage(CommandMessage, NetworkManager.ServerClientId, writer);
    }

    // Uses the operating system's route selection to find an IPv4 address to advertise.
    public static string GetLocalIp()
    {
        // Tries the address chosen by the routing table first.
        try
        {
            // Creates a temporary IPv4 datagram socket; protocol 0 asks the OS for the default.
            using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
            {
                // Sets a probe destination so the OS picks a local interface. This code sends no datagram; another destination may select a different adapter.
                s.Connect("8.8.8.8", 65530);
                // Reads the local address chosen for that route.
                return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
            }
        }
        // Falls back to enumerating adapters if selecting the route fails.
        catch (SocketException)
        {
            // Checks the system's available interfaces in their returned order.
            foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                // Skips interfaces that are not up.
                if (adapter.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                // Looks at addresses assigned to this interface.
                foreach (var entry in adapter.GetIPProperties().UnicastAddresses)
                    // Checks for IPv4 and excludes addresses that only point back to this device.
                    if (entry.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(entry.Address))
                        // Returns the first eligible IPv4 address.
                        return entry.Address.ToString();
            }
            // Falls back to loopback, which is only usable from the same machine.
            return "127.0.0.1";
        }
    }

    // Takes an IPv4 address as text and returns a shareable code or the original IPv4 address.
    public static string IpToCode(string ipStr)
    {
        // Uses loopback for invalid or non-IPv4 input.
        if (!IPAddress.TryParse(ipStr, out IPAddress ip) || ip.AddressFamily != AddressFamily.InterNetwork) ip = IPAddress.Loopback;
        // Splits IPv4 into four bytes, one per dotted number.
        byte[] b = ip.GetAddressBytes();
        // Returns the plain IPv4 text for first bytes without a prefix mapping.
        if (b[0] != 192 && b[0] != 10 && b[0] != 172 && b[0] != 127) return ip.ToString();
        // Packs the last three bytes into 24 bits and XORs with a fixed mask. Change the mask in both directions together; this is reversible encoding.
        uint val = (((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3]) ^ 0x5A3C91u;
        // Maps the first byte to C=192, A=10, B=172 or L=127. Keep the decoder mapping aligned.
        char prefix = b[0] == 192 ? 'C' : b[0] == 10 ? 'A' : b[0] == 172 ? 'B' : 'L';

        // Allocates six characters: one prefix and five base-36 digits.
        char[] res = new char[6];
        // Writes the first-byte marker.
        res[0] = prefix;
        // Fills positions 5 through 1 right to left using remainder and division by 36. Changing the base or length requires updating the decoder.
        for (int i = 5; i >= 1; i--) { res[i] = CHARS[(int)(val % 36)]; val /= 36; }
        // Converts the character array to the code shown in the UI.
        return new string(res);
    }

    // Takes a join code or dotted IPv4 text. Returns the destination address or null if invalid.
    public static string CodeToIp(string code)
    {
        // Rejects missing input before indexing it.
        if (string.IsNullOrWhiteSpace(code)) return null;
        // Ignores surrounding spaces and letter case in the input.
        code = code.Trim().ToUpperInvariant();
        // Treats dotted input as an address candidate.
        if (code.Contains("."))
            // Accepts the candidate only if it parses as IPv4.
            return IPAddress.TryParse(code, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? ip.ToString() : null;
        // Requires one prefix plus five encoded digits.
        if (code.Length != 6) return null;
        // Rejects prefixes not recognized by the encoder.
        if ("CABL".IndexOf(code[0]) < 0) return null;

        // Starts the base-36 accumulator at zero.
        ulong val = 0;
        // Reads each of the five digits from left to right.
        for (int i = 1; i < 6; i++)
        {
            // Gets the digit's numeric value from the shared alphabet.
            int idx = CHARS.IndexOf(code[i]);
            // Rejects any digit outside the alphabet.
            if (idx < 0) return null;
            // Shifts the accumulated number one base-36 place and appends the new digit.
            val = val * 36 + (ulong)idx;
        }

        // Rejects a number too large for the three packed IPv4 bytes.
        if (val > 0xFFFFFF) return null;
        // Reverses the encoder's XOR with the same mask.
        uint raw = ((uint)val) ^ 0x5A3C91u;
        // Decodes the prefix into the original first IPv4 byte.
        byte b0 = code[0] == 'C' ? (byte)192 : code[0] == 'A' ? (byte)10 : code[0] == 'B' ? (byte)172 : (byte)127;
        // Extracts the remaining three bytes using shifts and masks and formats a dotted address.
        return $"{b0}.{(raw >> 16) & 0xFF}.{(raw >> 8) & 0xFF}.{raw & 0xFF}";
    }
}
