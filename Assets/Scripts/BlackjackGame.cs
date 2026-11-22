using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class BlackjackGame : MonoBehaviour
{
    // GAME SETTINGS
    public const int TARGET_SCORE = 21;
    public const int DEALER_STOP = TARGET_SCORE - 4;    // dealer berhenti ambil kartu di 17
    public const int START_MONEY = 1000;
    public const int BET_AMOUNT = 100;                  // default value bet
    public const float DEALER_DELAY = 1f;               // jeda dealer ambil kartu
    public const int HIDDEN_CARD = -1;

    static readonly string[] SUITS = { "clubs", "diamonds", "hearts", "spades" };
    static readonly string[] RANKS = { "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K" };

    [Serializable]
    public class SeatData
    {
        public bool isOccupied;
        public int money = START_MONEY;
        public int bet = 0;
        public int nextBet = BET_AMOUNT;
        public PlayerStatus status = PlayerStatus.Waiting;
        public List<int> cards = new List<int>();
    }

    [Serializable]
    class TableState
    {
        public int phase;
        public int turnSeat;
        public List<int> dealerCards = new List<int>();
        public SeatData[] seats = new SeatData[4];
    }

    public GamePhase phase = GamePhase.Waiting;
    public int money;
    public int bet;
    public int nextBet = BET_AMOUNT;
    public PlayerStatus status = PlayerStatus.Waiting;
    public List<int> playerCards = new List<int>();
    public List<int> dealerCards = new List<int>();

    public SeatData[] seatsData = new SeatData[4];
    public int localSeat = 0;       // 0 = Seat 1, 1 = Seat 2, 2 = Seat 3, 3 = Seat 4
    public int turnSeat = -1;       // Giliran kursi mana sekarang (0 -> 1 -> 2 -> 3)
    public MultiplayerHost net;

    List<int> deck = new List<int>();
    int dealerHiddenCard;

    public event Action OnStateChanged;

    void Awake()
    {
        for (int i = 0; i < 4; i++) seatsData[i] = new SeatData { isOccupied = (i == 0) };

        net = GetComponent<MultiplayerHost>();
        if (net == null) net = gameObject.AddComponent<MultiplayerHost>();

        net.OnAssignSeat = AssignEmptySeat;
        net.OnClientCommand = OnClientCommand;
        net.OnClientLeft = OnClientLeft;
        net.OnJoinedAsClient = seat => { localSeat = seat; Changed(); };
        net.OnStateReceived = ApplyNetworkState;
    }

    void Changed()
    {
        SeatData my = seatsData[localSeat];
        money = my.money;
        bet = my.bet;
        nextBet = my.nextBet;
        status = my.status;
        playerCards = my.cards;

        if (net != null && net.isConnected && net.isHost)
        {
            string json = JsonUtility.ToJson(new TableState
            {
                phase = (int)phase,
                turnSeat = turnSeat,
                dealerCards = dealerCards,
                seats = seatsData
            });
            net.SendToClients(json);
        }

        OnStateChanged?.Invoke();
    }

    public string HostGame()
    {
        localSeat = 0;
        seatsData[0].isOccupied = true;
        string code = net.StartHost();
        Changed();
        return code;
    }

    public void JoinGame(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        string cleaned = code.Trim().ToUpperInvariant();
        if (net.isHost && cleaned == net.joinCode) return;

        net.Join(cleaned);
    }

    int AssignEmptySeat()
    {
        for (int i = 1; i < 4; i++)
        {
            if (!seatsData[i].isOccupied)
            {
                seatsData[i] = new SeatData { isOccupied = true };
                return i;
            }
        }
        return -1;
    }

    void OnClientLeft(int seat)
    {
        seatsData[seat].isOccupied = false;
        seatsData[seat].cards.Clear();
        if (phase == GamePhase.PlayerTurn && turnSeat == seat) NextTurn(seat);
        else Changed();
    }

    void OnClientCommand(int seat, string cmd)
    {
        if (cmd == "JOIN") Changed();
        else if (cmd == "HIT") HitForSeat(seat);
        else if (cmd == "STAND") StandForSeat(seat);
        else if (cmd == "LESS") LessBetForSeat(seat);
        else if (cmd == "MORE") MoreBetForSeat(seat);
    }

    void ApplyNetworkState(string json)
    {
        TableState s = JsonUtility.FromJson<TableState>(json);
        if (s == null) return;
        phase = (GamePhase)s.phase;
        turnSeat = s.turnSeat;
        dealerCards = s.dealerCards;
        seatsData = s.seats;
        Changed();
    }

    public bool IsHost() => net == null || !net.isConnected || net.isHost;
    public bool CanChangeBet() => phase == GamePhase.Waiting || phase == GamePhase.Result;

    public void LessBet()
    {
        if (!IsHost()) { net.SendToHost("LESS"); return; }
        LessBetForSeat(localSeat);
    }

    public void MoreBet()
    {
        if (!IsHost()) { net.SendToHost("MORE"); return; }
        MoreBetForSeat(localSeat);
    }

    void LessBetForSeat(int s)
    {
        if (!CanChangeBet() || seatsData[s].nextBet - BET_AMOUNT < BET_AMOUNT) return;
        seatsData[s].nextBet -= BET_AMOUNT;
        Changed();
    }

    void MoreBetForSeat(int s)
    {
        if (!CanChangeBet() || seatsData[s].nextBet + BET_AMOUNT > seatsData[s].money) return;
        seatsData[s].nextBet += BET_AMOUNT;
        Changed();
    }

    public void StartRound()
    {
        if (!IsHost()) return;
        if (phase == GamePhase.PlayerTurn || phase == GamePhase.DealerTurn) return;

        NewDeck();
        dealerCards.Clear();

        for (int i = 0; i < 4; i++)
        {
            SeatData s = seatsData[i];
            s.cards.Clear();

            if (s.isOccupied && s.money >= s.nextBet)
            {
                s.money -= s.nextBet;
                s.bet = s.nextBet;
                s.cards.Add(DrawCard());
                s.cards.Add(DrawCard());
                s.status = GetScore(s.cards) == TARGET_SCORE ? PlayerStatus.Blackjack : PlayerStatus.Playing;
            }
            else
            {
                s.bet = 0;
                s.status = PlayerStatus.Waiting;
            }
        }

        dealerCards.Add(DrawCard());
        dealerHiddenCard = DrawCard();
        dealerCards.Add(HIDDEN_CARD);

        phase = GamePhase.PlayerTurn;
        NextTurn(-1);
    }

    public void Hit()
    {
        if (phase != GamePhase.PlayerTurn || turnSeat != localSeat) return;
        if (!IsHost()) { net.SendToHost("HIT"); return; }
        HitForSeat(localSeat);
    }

    public void Stand()
    {
        if (phase != GamePhase.PlayerTurn || turnSeat != localSeat) return;
        if (!IsHost()) { net.SendToHost("STAND"); return; }
        StandForSeat(localSeat);
    }

    void HitForSeat(int s)
    {
        if (phase != GamePhase.PlayerTurn || turnSeat != s) return;
        SeatData seat = seatsData[s];
        seat.cards.Add(DrawCard());

        int score = GetScore(seat.cards);
        if (score > TARGET_SCORE) { seat.status = PlayerStatus.Bust; NextTurn(s); }
        else if (score == TARGET_SCORE) { seat.status = PlayerStatus.Blackjack; NextTurn(s); }
        else Changed();
    }

    void StandForSeat(int s)
    {
        if (phase != GamePhase.PlayerTurn || turnSeat != s) return;
        seatsData[s].status = PlayerStatus.Stand;
        NextTurn(s);
    }

    void NextTurn(int current)
    {
        for (int next = current + 1; next < 4; next++)
        {
            if (seatsData[next].isOccupied && seatsData[next].status == PlayerStatus.Playing)
            {
                turnSeat = next;
                Changed();
                return;
            }
        }

        turnSeat = -1;
        StartCoroutine(DealerTurn());
        Changed();
    }

    IEnumerator DealerTurn()
    {
        phase = GamePhase.DealerTurn;
        dealerCards[1] = dealerHiddenCard;
        Changed();
        yield return new WaitForSeconds(DEALER_DELAY);

        bool anyAlive = false;
        for (int i = 0; i < 4; i++)
            if (seatsData[i].bet > 0 && seatsData[i].status != PlayerStatus.Bust) anyAlive = true;

        while (anyAlive && GetScore(dealerCards) < DEALER_STOP)
        {
            dealerCards.Add(DrawCard());
            Changed();
            yield return new WaitForSeconds(DEALER_DELAY);
        }

        FinishRound();
    }

    void FinishRound()
    {
        int dealerScore = GetScore(dealerCards);

        for (int i = 0; i < 4; i++)
        {
            SeatData s = seatsData[i];
            if (s.bet == 0) continue;

            int pScore = GetScore(s.cards);
            if (pScore > TARGET_SCORE) s.status = PlayerStatus.Lose;
            else if (dealerScore > TARGET_SCORE || pScore > dealerScore) { s.status = PlayerStatus.Win; s.money += s.bet * 2; }
            else if (pScore == dealerScore) { s.status = PlayerStatus.Draw; s.money += s.bet; }
            else s.status = PlayerStatus.Lose;

            s.bet = 0;
            if (s.nextBet > s.money) s.nextBet = Mathf.Max(BET_AMOUNT, s.money);
        }

        phase = GamePhase.Result;
        Changed();
    }

    void NewDeck()
    {
        deck.Clear();
        for (int i = 0; i < 52; i++) deck.Add(i);
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            int temp = deck[i]; deck[i] = deck[j]; deck[j] = temp;
        }
    }

    int DrawCard()
    {
        if (deck.Count == 0) NewDeck();
        int card = deck[deck.Count - 1];
        deck.RemoveAt(deck.Count - 1);
        return card;
    }

    public static int GetValue(int cardId)
    {
        int rank = cardId % 13;
        if (rank == 0) return 11;
        if (rank >= 9) return 10;
        return rank + 1;
    }

    public static int GetScore(List<int> cards)
    {
        int total = 0, aceCount = 0;
        foreach (int card in cards)
        {
            if (card == HIDDEN_CARD) continue;
            total += GetValue(card);
            if (card % 13 == 0) aceCount++;
        }
        while (total > TARGET_SCORE && aceCount > 0) { total -= 10; aceCount--; }
        return total;
    }

    public static string GetSpriteName(int cardId)
    {
        if (cardId == HIDDEN_CARD) return "back_dark";
        return SUITS[cardId / 13] + "_" + RANKS[cardId % 13];
    }
}

public enum GamePhase { Waiting, PlayerTurn, DealerTurn, Result }
public enum PlayerStatus { Waiting, Playing, Stand, Bust, Blackjack, Win, Lose, Draw }
