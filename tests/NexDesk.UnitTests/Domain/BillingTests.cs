using NexDesk.Application.Billing;
using NexDesk.Domain.Billing;
using NexDesk.Infrastructure.Billing;

namespace NexDesk.UnitTests.Domain;

public sealed class BillingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    private static Plan PlanOf(string code) => Plan.Catalog.Single(p => p.Code == code);

    [Theory]
    [InlineData("4242 4242 4242 4242", "Visa", "4242")]
    [InlineData("5555-5555-5555-4444", "Mastercard", "4444")]
    [InlineData("378282246310005", "American Express", "0005")]
    [InlineData("6011111111111117", "Card", "1117")]
    public void A_card_number_with_a_valid_checksum_is_read_as_brand_and_last_four(string number, string brand, string last4) =>
        PaymentCard.Parse(number).ShouldBe(new PaymentCard(brand, last4));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4242 4242 4242 4241")]
    [InlineData("4242")]
    [InlineData("4242 4242 4242 424a")]
    [InlineData("42424242424242424242424242")]
    public void Anything_else_is_not_a_card(string? number) => PaymentCard.Parse(number).ShouldBeNull();

    [Theory]
    [InlineData(11, 2026, false)]
    [InlineData(12, 2026, false)]
    [InlineData(10, 2026, true)]
    [InlineData(12, 2025, true)]
    [InlineData(1, 2027, false)]
    [InlineData(0, 2030, true)]
    [InlineData(13, 2030, true)]
    public void A_card_is_good_through_the_end_of_its_expiry_month(int month, int year, bool expired) =>
        PaymentCard.IsExpired(month, year, Now).ShouldBe(expired);

    [Fact]
    public void The_declined_test_card_is_recognised_however_it_is_typed()
    {
        PaymentCard.IsDeclined("4000 0000 0000 0002").ShouldBeTrue();
        PaymentCard.IsDeclined("4000-0000-0000-0002").ShouldBeTrue();
        PaymentCard.IsDeclined("4242 4242 4242 4242").ShouldBeFalse();
        PaymentCard.Parse(PaymentCard.DeclinedNumber).ShouldNotBeNull();
    }

    [Fact]
    public void A_subscription_starts_free_and_gets_a_monthly_period_when_it_is_paid_for()
    {
        var subscription = Subscription.Start(Now);
        (subscription.PlanCode, subscription.CurrentPeriodEnd).ShouldBe((Plan.Free, null));
        subscription.IsDueForRenewal(Now.AddYears(5)).ShouldBeFalse();

        subscription.ChangeTo(PlanOf(Plan.Pro), new PaymentCard("Visa", "4242"), Now);
        subscription.CurrentPeriodEnd.ShouldBe(Now.AddMonths(1));
        subscription.IsDueForRenewal(Now.AddMonths(1).AddSeconds(-1)).ShouldBeFalse();
        subscription.IsDueForRenewal(Now.AddMonths(1)).ShouldBeTrue();

        subscription.Renew();
        (subscription.CurrentPeriodStart, subscription.CurrentPeriodEnd).ShouldBe((Now.AddMonths(1), Now.AddMonths(2)));
        subscription.CardLast4.ShouldBe("4242");

        subscription.ChangeTo(PlanOf(Plan.Free), null, Now.AddDays(40));
        (subscription.CurrentPeriodEnd, subscription.CardBrand, subscription.CardLast4).ShouldBe((null, null, null));
    }

    [Fact]
    public void An_invoice_bills_one_month_of_the_plan_and_is_paid_when_issued()
    {
        var subscription = Subscription.Start(Now);
        subscription.ChangeTo(PlanOf(Plan.Business), new PaymentCard("Visa", "4242"), Now);

        var invoice = Invoice.Issue(
            12, PlanOf(Plan.Business), subscription, InvoiceReason.PlanChange, "Acme", "olivia@acme.example", Now);

        (invoice.DisplayNumber, invoice.AmountCents, invoice.Currency, invoice.PlanName).ShouldBe(("INV-00012", 9900, "USD", "Business"));
        (invoice.PeriodStart, invoice.PeriodEnd, invoice.PaidAt).ShouldBe((Now, Now.AddMonths(1), Now));
        Invoice.FormatNumber(123456).ShouldBe("INV-123456");
    }

    [Fact]
    public void Usage_is_counted_per_calendar_month_in_utc()
    {
        UsageCounter.PeriodOf(Now).ShouldBe(202611);
        UsageCounter.PeriodOf(new DateTimeOffset(2027, 1, 1, 1, 0, 0, TimeSpan.FromHours(3))).ShouldBe(202612);
    }

    [Fact]
    public void The_plans_grow_with_their_price()
    {
        var plans = Plan.Catalog;
        plans.Select(p => p.Code).ShouldBe([Plan.Free, Plan.Pro, Plan.Business]);
        plans.Select(p => p.PriceCentsMonthly).ShouldBeInOrder();
        plans.Select(p => p.MaxStorageBytes).ShouldBeInOrder();
        plans.Select(p => p.RequestsPerMinute).ShouldBeInOrder();
        plans.Count(p => p.IsFree).ShouldBe(1);
    }

    [Theory]
    [InlineData("Acme Support")]
    [InlineData("شركة أكمي للدعم")]
    public void An_invoice_renders_as_a_pdf_for_latin_and_arabic_names(string workspaceName)
    {
        var pdf = new InvoicePdfRenderer().RenderPdf(new InvoicePrintModel(
            "INV-00001", workspaceName, workspaceName, "olivia@acme.example", "Pro", 2900, "USD",
            Now, Now, Now, Now.AddMonths(1), "Visa", "4242"));

        System.Text.Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-");
        pdf.Length.ShouldBeGreaterThan(2000);
    }
}
