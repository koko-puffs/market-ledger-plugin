using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketLedger.Core;

/// <summary>
/// What may be bought, against the run: on the market board, a listing at or under its line's
/// <c>maxPrice</c> for one, whose whole cost (tax included) fits what's left of the line's gil, of the
/// gil agreed for the run (<c>maxGil</c>), of your market ceiling and of your gil; at a vendor, the
/// line's price, within the same. Every purchase question the game asks is checked against the price
/// chosen before it's answered yes. Pure, so it's tested. (From Housing Studio's plugin's MarketRule,
/// without its leeway: the site's <c>maxPrice</c> already has room in it, the planned price and 15%.)
/// </summary>
public static class MarketRule
{
    /// <summary>The board's tax on a purchase (5%), when a listing doesn't say its own.</summary>
    public const double Tax = 0.05;

    /// <summary>A listing's whole cost with tax: its own tax when the game says it, else 5% rounded up.</summary>
    public static long Cost(uint unitPrice, int quantity, uint totalTax)
    {
        var total = (long)unitPrice * Math.Max(0, quantity);
        return total + (totalTax > 0 ? totalTax : TaxOn(total));
    }

    /// <summary>The board's 5% on <paramref name="total"/>, rounded up (in whole numbers: no rounding surprises).</summary>
    public static long TaxOn(long total) => total <= 0 ? 0 : (total + 19) / 20;

    /// <summary>
    /// The most a market line may spend: its amount at its <c>maxPrice</c>, tax included, and a stack
    /// bigger than needed (a listing is sold whole) only when that still fits.
    /// </summary>
    public static long LineBudget(int amount, long maxPrice) =>
        amount <= 0 || maxPrice <= 0 ? 0 : amount * maxPrice + TaxOn(amount * maxPrice);

    /// <summary>
    /// What's left to spend on the market this run: of the gil agreed (less all spent so far, vendors
    /// too), and of your ceiling for the market (0: no ceiling of your own).
    /// </summary>
    public static long MarketLeft(long maxGil, long spent, long ceiling, long marketSpent) =>
        Math.Max(0, Math.Min(maxGil - spent, ceiling > 0 ? ceiling - marketSpent : long.MaxValue));

    /// <summary>Why a listing isn't bought, or null when it may be.</summary>
    /// <param name="stillWanted">How many the line still needs in your bags.</param>
    /// <param name="lineGilLeft">What's left of the line's budget (<see cref="LineBudget"/> less what it spent).</param>
    /// <param name="runGilLeft">What's left of the run's gil and your ceiling (<see cref="MarketLeft"/>).</param>
    public static string? WhyNot(uint unitPrice, int quantity, uint totalTax, long maxPrice, int stillWanted, long lineGilLeft, long runGilLeft, long gilOnHand, bool hqWanted = false, bool hq = false)
    {
        if (quantity <= 0) return "nothing listed";
        if (stillWanted <= 0) return "enough already";
        if (hqWanted && !hq) return "it's normal quality, and the run wants high quality";
        if (unitPrice > maxPrice) return $"{unitPrice:N0} gil each is more than the {maxPrice:N0} the run allows";
        var cost = Cost(unitPrice, quantity, totalTax);
        if (cost > lineGilLeft) return $"{cost:N0} gil for {quantity} is more than the {lineGilLeft:N0} gil left for this line";
        if (cost > runGilLeft) return $"{cost:N0} gil would go past the gil agreed for the run ({runGilLeft:N0} left)";
        if (cost > gilOnHand) return $"{cost:N0} gil is more than you have ({gilOnHand:N0})";
        return null;
    }

    /// <summary>
    /// The listings to buy, in order: those that may be bought (<see cref="WhyNot"/>), cheapest for one
    /// first, each counted against what's left as it's taken, until the line has enough.
    /// </summary>
    public static List<int> Pick(IReadOnlyList<(uint UnitPrice, int Quantity, uint TotalTax, bool Hq)> listings, long maxPrice, int stillWanted,
        long lineGilLeft, long runGilLeft, long gilOnHand, bool hqWanted = false)
    {
        var picked = new List<int>();
        foreach (var i in Enumerable.Range(0, listings.Count).OrderBy(i => listings[i].UnitPrice).ThenBy(i => listings[i].Quantity))
        {
            var l = listings[i];
            if (WhyNot(l.UnitPrice, l.Quantity, l.TotalTax, maxPrice, stillWanted, lineGilLeft, runGilLeft, gilOnHand, hqWanted, l.Hq) != null) continue;
            var cost = Cost(l.UnitPrice, l.Quantity, l.TotalTax);
            picked.Add(i);
            stillWanted -= l.Quantity;
            lineGilLeft -= cost;
            runGilLeft -= cost;
            gilOnHand -= cost;
            if (stillWanted <= 0) break;
        }
        return picked;
    }

    /// <summary>Why a vendor purchase of <paramref name="count"/> at <paramref name="price"/> each isn't made, or null when it may be.</summary>
    public static string? VendorWhyNot(long price, int count, long maxGil, long spent, long gilOnHand)
    {
        if (count <= 0) return "enough already";
        var cost = price * count;
        if (spent + cost > maxGil) return $"{cost:N0} gil would go past the {maxGil:N0} gil agreed for the run ({Math.Max(0, maxGil - spent):N0} left)";
        if (cost > gilOnHand) return $"{cost:N0} gil is more than you have ({gilOnHand:N0})";
        return null;
    }

    /// <summary>
    /// A question names the purchase about to be made: one of its numbers is its cost with tax, its
    /// cost without, or its price for one. When none is (another row was clicked, or the question isn't
    /// the purchase's), the purchase isn't confirmed. The numbers are read with any grouping (1,234 /
    /// 1.234 / 1 234).
    /// </summary>
    public static bool QuestionMatches(string? question, long unitPrice, int quantity, long cost)
    {
        if (string.IsNullOrEmpty(question)) return false;
        var want = new[] { cost, unitPrice * quantity, unitPrice };
        foreach (var n in Numbers(question))
            if (Array.IndexOf(want, n) >= 0) return true;
        return false;
    }

    /// <summary>
    /// The board's question for the listing chosen ("Purchase 1 Cieldalaes planter for 7,229 gil (344 fee
    /// included)?"): its largest number is the listing's whole cost (with tax, 1 gil of rounding either
    /// way, or without tax when no fee is shown), and, for more than one, its quantity is in it.
    /// </summary>
    public static bool BoardQuestionMatches(string? question, int quantity, long cost, long costWithoutTax)
    {
        if (string.IsNullOrEmpty(question)) return false;
        var numbers = Numbers(question);
        if (numbers.Count == 0) return false;
        var total = numbers.Max();
        if (Math.Abs(total - cost) > 1 && total != costWithoutTax) return false;
        numbers.Remove(total);
        return quantity <= 1 || numbers.Contains(quantity);
    }

    /// <summary>
    /// A shop's question names the item: each of the name's words of 3 letters or more has its first 3
    /// in the question (so "leaves" says "leaf", "knives" "knife"); a name with no such word (two kanji)
    /// says nothing against it.
    /// </summary>
    public static bool MentionsItem(string? question, string itemName)
    {
        if (string.IsNullOrEmpty(question)) return false;
        var asked = string.Join(' ', Words(question));
        var words = Words(itemName).Where(w => w.Length >= 3).ToList();
        return words.Count == 0 || words.All(w => asked.Contains(w[..3], StringComparison.Ordinal));
    }

    private static IEnumerable<string> Words(string text) =>
        new string(text.ToLowerInvariant().Select(c => char.IsLetter(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The whole numbers in a text, a digit group's separators (, . space, no-break spaces) left out.</summary>
    public static List<long> Numbers(string text)
    {
        var list = new List<long>();
        long n = 0;
        var digits = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : '\0';
            if (c is >= '0' and <= '9')
            {
                if (digits < 18) n = n * 10 + (c - '0');
                digits++;
                continue;
            }
            // A separator between digit groups: the number goes on.
            if (digits > 0 && c is ',' or '.' or ' ' or ' ' or ' ' && i + 1 < text.Length && text[i + 1] is >= '0' and <= '9') continue;
            if (digits > 0) list.Add(n);
            n = 0;
            digits = 0;
        }
        return list;
    }

    /// <summary>How many new bag slots adding <paramref name="adding"/> to <paramref name="held"/> takes, stacks of <paramref name="stack"/>.</summary>
    public static int NewSlots(int held, int adding, int stack) =>
        adding <= 0 ? 0 : Stacks(Math.Max(0, held) + adding, stack) - Stacks(Math.Max(0, held), stack);

    private static int Stacks(int count, int stack) => count <= 0 ? 0 : (count + Math.Max(1, stack) - 1) / Math.Max(1, stack);
}
