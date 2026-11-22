using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class TableUI : MonoBehaviour
{
    [Serializable]
    public class Seat
    {
        public Image background;
        public Text nameText;
        public Transform cardArea;
        public Text infoText;
    }

    public BlackjackGame game;

    public Seat[] seats;
    public Text dealerText;
    public Transform dealerCardArea;
    public Text infoText;

    public Button hitButton;
    public Button standButton;
    public Button startButton;
    public Button lessBetButton;
    public Button moreBetButton;

    // Hubungkan ke HostButton, JoinButton dan Join Text (InputField) yang ada di scene
    public Button hostButton;
    public Button joinButton;
    public InputField joinTextInput;

    public Vector2 cardSize = new Vector2(90, 126);
    public Color seatColor = new Color(0f, 0f, 0f, 0.35f);     // warna kursi default
    public Color turnColor = new Color(1f, 0.85f, 0f, 0.5f);   // warna kursi saat turn player pov

    public float disabledTextAlpha = 0.4f;

    bool needRefresh = true;
    Dictionary<string, Sprite> spriteCache = new Dictionary<string, Sprite>();

    void Awake()
    {
        hitButton.onClick.AddListener(game.Hit);
        standButton.onClick.AddListener(game.Stand);
        startButton.onClick.AddListener(game.StartRound);
        lessBetButton.onClick.AddListener(game.LessBet);
        moreBetButton.onClick.AddListener(game.MoreBet);

        if (hostButton != null)
        {
            hostButton.onClick.AddListener(() =>
            {
                string code = game.HostGame();
                if (joinTextInput != null && !string.IsNullOrEmpty(code)) joinTextInput.text = code;
            });
        }

        if (joinButton != null)
        {
            joinButton.onClick.AddListener(() =>
            {
                if (joinTextInput != null) game.JoinGame(joinTextInput.text);
            });
        }

        game.OnStateChanged += () => needRefresh = true;
    }

    void Update()
    {
        if (needRefresh)
        {
            needRefresh = false;
            Refresh();
        }
    }

    void Refresh()
    {
        // Dealer
        ShowCards(dealerCardArea, game.dealerCards);
        dealerText.text = game.dealerCards.Count > 0 ? "DEALER   Score: " + BlackjackGame.GetScore(game.dealerCards) : "DEALER";

        // Tampilkan Seat 1 - Seat 4
        for (int i = 0; i < seats.Length; i++)
        {
            if (i < 4 && game.seatsData[i].isOccupied)
            {
                bool isTurn = (game.phase == GamePhase.PlayerTurn && game.turnSeat == i);
                string name = (i == game.localSeat) ? $"Player {i + 1} (You)" : $"Player {i + 1}";
                ShowPlayer(seats[i], game.seatsData[i], name, isTurn);
            }
            else
            {
                ShowEmptySeat(seats[i], i);
            }
        }

        infoText.text = GetInfoText();

        bool myTurn = (game.phase == GamePhase.PlayerTurn && game.turnSeat == game.localSeat);
        BlackjackGame.SeatData mySeat = game.seatsData[game.localSeat];

        SetButton(hitButton, myTurn);
        SetButton(standButton, myTurn);
        SetButton(startButton, game.IsHost() && (game.phase == GamePhase.Waiting || game.phase == GamePhase.Result));

        SetButton(lessBetButton, game.CanChangeBet() && mySeat.nextBet > BlackjackGame.BET_AMOUNT);
        SetButton(moreBetButton, game.CanChangeBet() && mySeat.nextBet + BlackjackGame.BET_AMOUNT <= mySeat.money);

        // Host / Join hanya bisa diklik kalau belum terhubung
        bool online = game.net != null && (game.net.isConnected || game.net.IsJoining);
        if (hostButton != null) SetButton(hostButton, !online);
        if (joinButton != null) SetButton(joinButton, !online && game.CanChangeBet());
    }

    void SetButton(Button button, bool isOn)
    {
        button.interactable = isOn;
        Text text = button.GetComponentInChildren<Text>();
        Color color = text.color;
        color.a = isOn ? 1f : disabledTextAlpha;
        text.color = color;
    }

    void ShowPlayer(Seat seat, BlackjackGame.SeatData data, string playerName, bool isTurn)
    {
        seat.nameText.text = playerName;

        string info = "Money: " + data.money;
        info += "   Bet: " + (data.bet > 0 ? data.bet : data.nextBet);
        if (data.cards.Count > 0) info += "\nScore: " + BlackjackGame.GetScore(data.cards);
        info += "\n" + data.status;
        seat.infoText.text = info;

        seat.background.color = isTurn ? turnColor : seatColor;
        ShowCards(seat.cardArea, data.cards);
    }

    void ShowEmptySeat(Seat seat, int index)
    {
        seat.nameText.text = "Seat " + (index + 1);
        seat.infoText.text = "Empty";
        seat.background.color = seatColor;
        ShowCards(seat.cardArea, new List<int>());
    }

    string GetInfoText()
    {
        if (game.net != null && (game.net.IsJoining || !game.net.isConnected)
            && !string.IsNullOrEmpty(game.net.ConnectionMessage))
            return game.net.ConnectionMessage;
        bool roundOver = game.phase == GamePhase.Waiting || game.phase == GamePhase.Result;
        if (roundOver && game.money < BlackjackGame.BET_AMOUNT) return "Game Over";

        switch (game.phase)
        {
            case GamePhase.Waiting:
                if (game.net != null && !game.net.isConnected) return "Host a LAN table, enter a code or IP to join, or start solo.";
                if (game.net != null && game.net.isHost) return game.net.ConnectionMessage + " Press START when ready.";
                return game.IsHost() ? "Klik START untuk mulai" : "Tunggu Host klik START";
            case GamePhase.PlayerTurn: return game.turnSeat == game.localSeat ? "Your Turn (Hit or Stand)" : $"Player {game.turnSeat + 1} Turn";
            case GamePhase.DealerTurn: return "Dealer Turn";
            case GamePhase.Result: return "You " + game.status + "!";
        }
        return "";
    }

    void ShowCards(Transform area, List<int> cards)
    {
        foreach (Transform child in area) Destroy(child.gameObject);

        foreach (int card in cards)
        {
            GameObject cardObject = new GameObject("Card", typeof(RectTransform), typeof(Image));
            cardObject.transform.SetParent(area, false);
            cardObject.GetComponent<RectTransform>().sizeDelta = cardSize;
            cardObject.GetComponent<Image>().sprite = GetSprite(card);
        }
    }

    Sprite GetSprite(int card)
    {
        string spriteName = BlackjackGame.GetSpriteName(card);
        if (!spriteCache.ContainsKey(spriteName))
        {
            spriteCache[spriteName] = Resources.Load<Sprite>("Cards/" + spriteName);
        }
        return spriteCache[spriteName];
    }
}
