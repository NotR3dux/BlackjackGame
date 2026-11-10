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

        // Player di Seat 1 (0)
        bool myTurn = game.phase == GamePhase.PlayerTurn;
        ShowPlayer(seats[0], "Player 1 (You)", myTurn);
        for (int i = 1; i < seats.Length; i++)
        {
            ShowEmptySeat(seats[i], i);
        }

        infoText.text = GetInfoText();

        SetButton(hitButton, myTurn);
        SetButton(standButton, myTurn);
        SetButton(startButton, game.phase == GamePhase.Waiting || game.phase == GamePhase.Result);

        // LESS BET mati kalau bet udah 100, MORE BET mati kalau bet udah sama dengan uang
        SetButton(lessBetButton, game.CanChangeBet() && game.nextBet > BlackjackGame.BET_AMOUNT);
        SetButton(moreBetButton, game.CanChangeBet() && game.nextBet + BlackjackGame.BET_AMOUNT <= game.money);
    }

    // Nyala matiin tombol
    void SetButton(Button button, bool isOn)
    {
        button.interactable = isOn;

        Text text = button.GetComponentInChildren<Text>();
        Color color = text.color;
        color.a = isOn ? 1f : disabledTextAlpha;
        text.color = color;
    }

    void ShowPlayer(Seat seat, string playerName, bool isTurn)
    {
        seat.nameText.text = playerName;

        string info = "Money: " + game.money;
        info += "   Bet: " + (game.bet > 0 ? game.bet : game.nextBet);  
        if (game.playerCards.Count > 0) info += "\nScore: " + BlackjackGame.GetScore(game.playerCards);
        info += "\n" + game.status;
        seat.infoText.text = info;

        seat.background.color = isTurn ? turnColor : seatColor;
        ShowCards(seat.cardArea, game.playerCards);
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
        if (roundOver && game.money < BlackjackGame.BET_AMOUNT)
        {
            return "Game Over";
        }

        switch (game.phase)
        {
            case GamePhase.Waiting: return "Klik START untuk mulai";
            case GamePhase.PlayerTurn: return "Your Turn (Hit or Stand)";
            case GamePhase.DealerTurn: return "Dealer Turn";
            case GamePhase.Result: return "You " + game.status + "!";
        }
        return "";
    }

    // Hapus kartu lama terus buat kartu baru dari assets
    void ShowCards(Transform area, List<int> cards)
    {
        foreach (Transform child in area)
        {
            Destroy(child.gameObject);
        }

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
