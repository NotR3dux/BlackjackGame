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

    // 3 elemen UI simpel buat Multiplayer (otomatis dibuat kalau kosong di Inspector)
    public Button showCodeHostButton;
    public InputField codeInputField;
    public Button joinCodeButton;

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

        game.OnStateChanged += () => needRefresh = true;

        SetupSimpleMultiplayerUI();
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
        bool roundOver = game.phase == GamePhase.Waiting || game.phase == GamePhase.Result;
        if (roundOver && game.money < BlackjackGame.BET_AMOUNT) return "Game Over";

        switch (game.phase)
        {
            case GamePhase.Waiting: return game.IsHost() ? "Klik START untuk mulai" : "Tunggu Host klik START";
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

    // Buat 1 tombol Show Code (Host), 1 tempat ketik Code, dan 1 tombol Join Code
    void SetupSimpleMultiplayerUI()
    {
        if (showCodeHostButton == null || codeInputField == null || joinCodeButton == null)
        {
            Canvas canvas = GetComponentInParent<Canvas>() ?? FindFirstObjectByType<Canvas>();
            if (canvas == null) return;
            Font font = infoText != null ? infoText.font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            showCodeHostButton = MakeButton(canvas.transform, font, "SHOW CODE (HOST)", new Vector2(20, -20));
            codeInputField = MakeInput(canvas.transform, font, "ENTER CODE", new Vector2(210, -20));
            joinCodeButton = MakeButton(canvas.transform, font, "JOIN CODE", new Vector2(400, -20));
        }

        showCodeHostButton.onClick.AddListener(() =>
        {
            string code = game.HostGame();
            showCodeHostButton.GetComponentInChildren<Text>().text = "CODE: " + code;
            codeInputField.text = code;
        });

        joinCodeButton.onClick.AddListener(() =>
        {
            game.JoinGame(codeInputField.text);
            joinCodeButton.GetComponentInChildren<Text>().text = "JOINED";
        });
    }

    Button MakeButton(Transform parent, Font font, string label, Vector2 pos)
    {
        GameObject go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(180, 40);
        go.GetComponent<Image>().color = new Color(0.15f, 0.45f, 0.25f, 0.95f);

        GameObject txt = new GameObject("Text", typeof(RectTransform), typeof(Text));
        txt.transform.SetParent(go.transform, false);
        RectTransform tr = txt.GetComponent<RectTransform>();
        tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.sizeDelta = Vector2.zero;

        Text t = txt.GetComponent<Text>();
        t.font = font; t.text = label; t.fontSize = 15; t.alignment = TextAnchor.MiddleCenter; t.color = Color.white;
        return go.GetComponent<Button>();
    }

    InputField MakeInput(Transform parent, Font font, string placeholder, Vector2 pos)
    {
        GameObject go = new GameObject("CodeInput", typeof(RectTransform), typeof(Image), typeof(InputField));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(180, 40);
        go.GetComponent<Image>().color = new Color(0.15f, 0.15f, 0.15f, 0.95f);

        GameObject txt = new GameObject("Text", typeof(RectTransform), typeof(Text));
        txt.transform.SetParent(go.transform, false);
        RectTransform tr = txt.GetComponent<RectTransform>();
        tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.sizeDelta = new Vector2(-10, 0);

        Text t = txt.GetComponent<Text>();
        t.font = font; t.fontSize = 16; t.alignment = TextAnchor.MiddleCenter; t.color = Color.yellow;

        GameObject ph = new GameObject("Placeholder", typeof(RectTransform), typeof(Text));
        ph.transform.SetParent(go.transform, false);
        RectTransform pr = ph.GetComponent<RectTransform>();
        pr.anchorMin = Vector2.zero; pr.anchorMax = Vector2.one; pr.sizeDelta = new Vector2(-10, 0);

        Text pt = ph.GetComponent<Text>();
        pt.font = font; pt.text = placeholder; pt.fontSize = 14; pt.alignment = TextAnchor.MiddleCenter; pt.color = Color.gray;

        InputField input = go.GetComponent<InputField>();
        input.textComponent = t; input.placeholder = pt;
        return input;
    }
}
