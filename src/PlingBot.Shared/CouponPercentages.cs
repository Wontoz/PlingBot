namespace PlingBot.Services;

// Delat resultat av svenska-folket-procent + odds-parsning. Används av både
// TipsScraper (initial skrapning) och CouponPercentageService (löpande uppdatering).
public class CouponPercentages
{
    public int? One { get; set; }
    public int? X { get; set; }
    public int? Two { get; set; }
    public decimal? Odds1 { get; set; }
    public decimal? OddsX { get; set; }
    public decimal? Odds2 { get; set; }
    public bool HasAllValues => One.HasValue && X.HasValue && Two.HasValue;
}
