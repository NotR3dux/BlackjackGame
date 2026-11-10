using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class BlackjackGame : MonoBehaviour
{
    // GAME SETTINGS
    public const int TARGET_SCORE = 21;                 
    public const int DEALER_STOP = TARGET_SCORE - 4;    // dealer berhenti ambil kartu di 17 (karena sweet spot, kalo lebih maka dealer sering bust (rule di casino btw))
    public const int START_MONEY = 1000;
    public const int BET_AMOUNT = 100;                  // default value bet
    public const float DEALER_DELAY = 1f;               // jeda dealer ambil kartu (buat penampilan, biar lebih menegangkan :) )

    // Kartu disimpan sebagai angka 0 - 51
    // cardId / 13 = suit (hasil dibagi 13 dibulatkan kebawah nya = 0 clubs, 1 diamonds, 2 hearts, 3 spades)
    // cardId % 13 = rank (sisa dari dibagi 13 nya = 0 = A, 1 = 2, ... 9 = 10, 10 = J, 11 = Q, 12 = K)   
    // -1 = kartu tertutup (buat yg di display itu face belakang kartunya)
    public const int HIDDEN_CARD = -1;
    static readonly string[] SUITS = { "clubs", "diamonds", "hearts", "spades" };
    static readonly string[] RANKS = { "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K" };

    public GamePhase phase = GamePhase.Waiting;
    public int money;
    public int bet;
    public int nextBet = BET_AMOUNT;   
    public PlayerStatus status = PlayerStatus.Waiting;
    public List<int> playerCards = new List<int>();
    public List<int> dealerCards = new List<int>();

    List<int> deck = new List<int>();
    int dealerHiddenCard;

    public event Action OnStateChanged;

    void Start()
    {
        money = START_MONEY;
        Changed();
    }

    void Changed()
    {
        OnStateChanged?.Invoke();
    }

    // Bet cuma bisa diubah sebelum round mulai or setelah round selesai
    public bool CanChangeBet()
    {
        return phase == GamePhase.Waiting || phase == GamePhase.Result;
    }

    public void LessBet()
    {
        if (!CanChangeBet()) return;
        if (nextBet - BET_AMOUNT < BET_AMOUNT) return;

        nextBet -= BET_AMOUNT;
        Changed();
    }

    public void MoreBet()
    {
        if (!CanChangeBet()) return;
        if (nextBet + BET_AMOUNT > money) return;

        nextBet += BET_AMOUNT;
        Changed();
    }


    public void StartRound()
    {
        if (phase == GamePhase.PlayerTurn || phase == GamePhase.DealerTurn) return;
        if (money < nextBet) return;

        NewDeck();
        playerCards.Clear();
        dealerCards.Clear();

        money -= nextBet;
        bet = nextBet;
        status = PlayerStatus.Playing;

        // Bagi 2 kartu ke player dan dealer, tapi kartu ke 2 dealer tertutup (-1)
        playerCards.Add(DrawCard());
        playerCards.Add(DrawCard());
        dealerCards.Add(DrawCard());
        dealerHiddenCard = DrawCard();
        dealerCards.Add(HIDDEN_CARD);

        phase = GamePhase.PlayerTurn;

        if (GetScore(playerCards) == TARGET_SCORE)
        {
            status = PlayerStatus.Blackjack;
            StartCoroutine(DealerTurn());
        }

        Changed();
    }


    public void Hit()
    {
        if (phase != GamePhase.PlayerTurn) return;

        playerCards.Add(DrawCard());

        int score = GetScore(playerCards);
        if (score > TARGET_SCORE)
        {
            status = PlayerStatus.Bust;
            StartCoroutine(DealerTurn());
        }
        else if (score == TARGET_SCORE)
        {
            status = PlayerStatus.Blackjack;
            StartCoroutine(DealerTurn());
        }

        Changed();
    }


    public void Stand()
    {
        if (phase != GamePhase.PlayerTurn) return;

        status = PlayerStatus.Stand;
        StartCoroutine(DealerTurn());
        Changed();
    }

    IEnumerator DealerTurn()
    {
        phase = GamePhase.DealerTurn;

        // Buka kartu dealer yg hiiden
        dealerCards[1] = dealerHiddenCard;
        Changed();
        yield return new WaitForSeconds(DEALER_DELAY);

        // Dealer ambil kartu sampai DEALER_STOP (kecuali pemain bust)
        while (status != PlayerStatus.Bust && GetScore(dealerCards) < DEALER_STOP)
        {
            dealerCards.Add(DrawCard());
            Changed();
            yield return new WaitForSeconds(DEALER_DELAY);
        }

        FinishRound();
    }

    void FinishRound()
    {
        int playerScore = GetScore(playerCards);
        int dealerScore = GetScore(dealerCards);

        if (playerScore > TARGET_SCORE)
        {
            status = PlayerStatus.Lose;
        }
        else if (dealerScore > TARGET_SCORE || playerScore > dealerScore)
        {
            status = PlayerStatus.Win;
            money += bet * 2;   
        }
        else if (playerScore == dealerScore)
        {
            status = PlayerStatus.Draw;
            money += bet;       
        }
        else
        {
            status = PlayerStatus.Lose;
        }

        bet = 0;

        // Kalau uang sisa lebih kecil dari bet yang dipilih, turunin bet-nya
        if (nextBet > money) nextBet = Mathf.Max(BET_AMOUNT, money);

        phase = GamePhase.Result;
        Changed();
    }

    // Deck (52 kartu)
    void NewDeck()
    {
        deck.Clear();
        for (int i = 0; i < 52; i++) deck.Add(i);

        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            int temp = deck[i];
            deck[i] = deck[j];
            deck[j] = temp;
        }
    }

    int DrawCard()
    {
        if (deck.Count == 0) NewDeck();

        int card = deck[deck.Count - 1];
        deck.RemoveAt(deck.Count - 1);
        return card;
    }

    // buat ubah dari card id jd value atau scorenya 
    public static int GetValue(int cardId)
    {
        int rank = cardId % 13;
        if (rank == 0) return 11;
        if (rank >= 9) return 10;
        return rank + 1;
    }

    // hitung total score. if lewat TARGET_SCORE, kartu A dihitung 1 (karena A itu bisa 1 atau 11)
    public static int GetScore(List<int> cards)
    {
        int total = 0;
        int aceCount = 0;

        foreach (int card in cards)
        {
            if (card == HIDDEN_CARD) continue;
            total += GetValue(card);
            if (card % 13 == 0) aceCount++;
        }

        while (total > TARGET_SCORE && aceCount > 0)
        {
            total -= 10;
            aceCount--;
        }

        return total;
    }

    public static string GetSpriteName(int cardId)
    {
        if (cardId == HIDDEN_CARD) return "back_dark";
        return SUITS[cardId / 13] + "_" + RANKS[cardId % 13];
    }
}

public enum GamePhase
{
    Waiting,     // tunggu start round
    PlayerTurn,  // giliran pemain
    DealerTurn,  // giliran dealer
    Result       // ronde selesai
}

public enum PlayerStatus
{
    Waiting,
    Playing,
    Stand,
    Bust,        // lewat TARGET_SCORE
    Blackjack,   // pas di TARGET_SCORE (lgsg menang)
    Win,
    Lose,
    Draw         
}
