using NexDesk.Domain.Identity;
using NexDesk.Domain.Tickets;

namespace NexDesk.Infrastructure.Seeding;

internal sealed record DemoPerson(string Key, string FullName, string Email, Role Role);

internal sealed record DemoTeam(string Key, string Name, string Description, string[] MemberKeys);

/// <summary>A kind of request customers send, with the conversation that usually follows.</summary>
internal sealed record DemoTopic(
    string TeamKey,
    TicketType Type,
    string[] Tags,
    string[] Subjects,
    string Description,
    string AgentReply,
    string CustomerFollowUp,
    string ClosingReply,
    string? InternalNote = null,
    string? ProductArea = null)
{
    /// <summary>The values filled into this copy of the topic, for one ticket.</summary>
    public IReadOnlyDictionary<string, string>? Variables { get; init; }
}

internal sealed record DemoArticle(string Category, string Title, string Body, int Views, int Helpful, int NotHelpful);

internal sealed record DemoWorkspace(
    string Name,
    string Slug,
    string Language,
    string Timezone,
    bool WeekStartsOnSunday,
    string? BrandColor,
    string WelcomeText,
    DemoPerson[] People,
    DemoTeam[] Teams,
    DemoTopic[] Topics,
    (string Name, string Description)[] Categories,
    DemoArticle[] Articles,
    (int Day, string Name)[] Holidays,
    string[] CustomerNames,
    string CustomerDomain,
    string PortalCustomerEmail,
    string PortalCustomerName,
    (int Day, string Plan)[] PlanChanges,
    double TicketsPerDay)
{
    /// <summary>How far back the history goes; the size's default when null.</summary>
    public int? Days { get; init; }

    /// <summary>Custom fields, each filled from one of the ticket's variables.</summary>
    public DemoField[]? Fields { get; init; }

    /// <summary>Addresses for <see cref="CustomerNames"/>, in the same order.</summary>
    public string[]? CustomerEmails { get; init; }

    /// <summary>Values such as {city} or {ref} that differ from ticket to ticket and fill in the topic texts.</summary>
    public Func<Random, Dictionary<string, string>>? Variables { get; init; }

    /// <summary>Also gives the workspace an API key, a webhook with deliveries, saved views and an open invitation.</summary>
    public bool Extras { get; init; }
}

internal sealed record DemoField(string Key, string Label, CustomFieldType Type, string[]? Options, string Variable);

/// <summary>Everything the demo workspaces say. Kept apart from the seeding logic so it reads as content.</summary>
internal static class DemoContent
{
    public const string Password = "NexDesk-demo-2026";
    public const string PlatformAdminEmail = "admin@nexdesk.demo";

    public static readonly DemoWorkspace BrightPath = new(
        Name: "BrightPath Software",
        Slug: "brightpath",
        Language: "en",
        Timezone: "Europe/London",
        WeekStartsOnSunday: false,
        BrandColor: "#1e6091",
        WelcomeText: "Hi there! Search our guides below, or send us a request and the BrightPath team will get back to you within a working day.",
        People:
        [
            new("owner", "Sarah Mitchell", "sarah@brightpath.demo", Role.Owner),
            new("admin", "Daniel Reyes", "daniel@brightpath.demo", Role.Admin),
            new("maya", "Maya Chen", "maya@brightpath.demo", Role.Agent),
            new("tom", "Tom Okafor", "tom@brightpath.demo", Role.Agent),
            new("priya", "Priya Nair", "priya@brightpath.demo", Role.Agent),
            new("leo", "Leo Hartmann", "leo@brightpath.demo", Role.Agent),
            new("finance", "Grace Lee", "finance@brightpath.demo", Role.Viewer),
        ],
        Teams:
        [
            new("billing", "Billing", "Invoices, payments, refunds and plan changes.", ["maya", "tom"]),
            new("tech", "Technical support", "Bugs, integrations and anything that does not work as it should.", ["priya", "leo", "tom"]),
            new("success", "Customer success", "Onboarding, training and feature questions.", ["maya", "priya"]),
        ],
        Topics:
        [
            new("billing", TicketType.Question, ["billing", "invoice"],
                ["Invoice shows the wrong company name", "Can I get a copy of last month's invoice?", "VAT number missing from our invoice", "Charged twice this month"],
                "Hello,\n\nOur latest invoice has an issue and our finance team cannot process it. Could you take a look and send a corrected copy?\n\nThanks",
                "Thanks for flagging this. I have corrected the billing details on your account and attached a fresh copy of the invoice. Let me know if anything else looks off.",
                "That looks right now, thank you. Will future invoices use the new details as well?",
                "Yes, every invoice from now on uses the updated details. I'll mark this as solved.",
                "Checked Stripe: one duplicate charge, refund issued.", "Billing"),
            new("billing", TicketType.Task, ["billing", "plan-change"],
                ["Upgrade us to the Growth plan", "Downgrade request for next billing cycle", "Add five more seats to our account"],
                "Hi team,\n\nWe'd like to change our subscription. What's the best way to do this without losing our data?",
                "Happy to help. I've applied the change to your account; it takes effect immediately and nothing is lost. You'll see a prorated line on your next invoice.",
                "Great, and do the new seats need to be invited separately?",
                "They do: invite them from Settings > Users and they'll get an email. Closing this one, but reply any time.",
                null, "Billing"),
            new("tech", TicketType.Problem, ["login", "sso"],
                ["Can't sign in with Google SSO", "Password reset email never arrives", "Two-factor code rejected every time", "Locked out after changing my email"],
                "Hi,\n\nSince this morning I can't sign in. I've tried two browsers and cleared my cache. The page just reloads with no error.",
                "Sorry about that! I can see repeated sign-in attempts on our side. Could you try the link I've just sent you, which resets your session?",
                "That worked, I'm back in. What caused it?",
                "A stale session after our identity provider rotated its keys. We've added a fix so it won't happen again. Solving this now.",
                "Session table had 3 stale entries for this user; cleared manually.", "Inbox"),
            new("tech", TicketType.Incident, ["bug", "reports"],
                ["Dashboard charts are empty", "CSV export cuts off after 1000 rows", "Report totals don't match the ticket list", "Weekly report email shows last month's data"],
                "Hello,\n\nThe reports page shows no data for the last week even though we handled plenty of tickets. Screenshot attached.",
                "Thanks for the details. I've reproduced it: a time zone setting is shifting the range. Our engineers are on it and I'll update you shortly.",
                "Any news? Our weekly review is tomorrow.",
                "The fix is live. Reports now show the right range; please refresh. Thanks for your patience!",
                "Linked to engineering issue ENG-2214.", "Reports"),
            new("tech", TicketType.Problem, ["integration", "api"],
                ["Webhook calls failing with 401", "Slack integration stopped posting", "API rate limit hit during nightly sync", "Zapier trigger not firing"],
                "Hi,\n\nOur integration stopped working overnight. Nothing changed on our side as far as we can tell.",
                "I checked the delivery log: the requests are reaching an endpoint that now rejects them. Did anyone rotate the credentials recently?",
                "Our IT team rotated the keys yesterday, that must be it. We've updated them now.",
                "Deliveries are succeeding again since 10:42. Marking this as solved.",
                null, "Integrations"),
            new("success", TicketType.Question, ["how-to"],
                ["How do I set up business hours?", "Can we route tickets by language?", "Best way to share a saved view with the team", "How do automation rules decide the order?"],
                "Hi,\n\nWe're new to BrightPath and want to get the most out of it. Could you point us in the right direction?",
                "Great question! I've linked our step-by-step guide on this. In short: Settings, then pick the section, and changes apply straight away.",
                "Thanks, that's clear. Can admins change it later?",
                "Absolutely, admins can change it any time. Enjoy, and reach out if anything else comes up!",
                null, "Inbox"),
            new("success", TicketType.Task, ["feature-request"],
                ["Feature request: dark mode for the mobile app", "Could you add a Spanish help centre?", "Request: export customers as CSV", "Suggestion: bulk close old tickets"],
                "Hello,\n\nWe'd love to see this in BrightPath. It would save our team a lot of time every week.",
                "Thanks for the suggestion! I've added your vote to our roadmap item and our product team reviews these every sprint.",
                "Great, is there a way to follow progress?",
                "Yes, I've subscribed you to updates on the roadmap item. You'll get an email when it ships.",
                "Roadmap card PM-118; 23 other customers asked for this.", "Mobile app"),
        ],
        Categories:
        [
            ("Getting started", "Set up your workspace and invite your team."),
            ("Billing and plans", "Invoices, payment methods and changing plans."),
            ("Integrations", "Connect BrightPath to the tools you already use."),
        ],
        Articles:
        [
            new("Getting started", "Invite your team",
                "## Who can invite people\n\nOwners and admins can invite teammates from **Settings > Users**.\n\n1. Click **Invite**.\n2. Enter their email and choose a role.\n3. They get an email with a link that works for 7 days.\n\nAgents answer tickets, viewers can only read, and admins manage settings.", 412, 38, 2),
            new("Getting started", "Set your business hours",
                "SLA timers only count the hours your team works.\n\n- Go to **SLA**.\n- Pick your time zone.\n- Switch each day on or off and set the opening hours.\n- Add public holidays so they never count against you.", 268, 21, 1),
            new("Getting started", "Write replies faster with saved views",
                "Saved views remember a set of filters, such as *urgent and unassigned*. Share a view with your team so everyone works from the same queue.", 187, 15, 3),
            new("Billing and plans", "Change your plan",
                "You can upgrade or downgrade at any time from **Plan and billing**.\n\nUpgrades start immediately with a fresh monthly period. Downgrades take effect straight away; nothing is deleted, but limits apply to new work.", 356, 30, 4),
            new("Billing and plans", "Download an invoice",
                "Every payment creates an invoice. Open **Plan and billing**, find it in the list and choose **Download PDF**.", 298, 27, 1),
            new("Integrations", "Connect with webhooks",
                "Webhooks post a signed JSON message to your server whenever a ticket is created, updated or solved.\n\n```\nX-NexDesk-Signature: t=1700000000,v1=...\n```\n\nVerify the HMAC-SHA256 signature with your endpoint's secret before trusting a request.", 144, 12, 2),
            new("Integrations", "Use the public API",
                "Create an API key under **Developers**, give it only the scopes it needs, and send it in the `X-Api-Key` header.", 203, 17, 1),
        ],
        Holidays: [(42, "Summer bank holiday")],
        CustomerNames:
        [
            "Casey Morgan", "Jordan Blake", "Alex Rivera", "Sam Patel", "Taylor Brooks", "Morgan Ellis", "Jamie Fox",
            "Riley Quinn", "Avery Stone", "Drew Carter", "Charlie Hughes", "Skyler Grant", "Emerson Park", "Finley Ross",
            "Harper Young", "Kendall Price", "Logan Ward", "Parker Bell", "Quinn Foster", "Reese Collins", "Rowan Hayes",
            "Sydney Turner", "Blake Murphy", "Cameron Reid", "Dakota Shaw", "Elliot Moore", "Frankie James", "Hayden Cole",
            "Jesse Kim", "Kai Warren", "Lane Fisher", "Marley Dean", "Noel Barker", "Oakley Hunt", "Peyton Lane", "Remy Fraser",
        ],
        CustomerDomain: "customers.demo",
        PortalCustomerEmail: "casey@customers.demo",
        PortalCustomerName: "Casey Morgan",
        PlanChanges: [(4, "pro"), (38, "business")],
        TicketsPerDay: 2.6);

    public static readonly DemoWorkspace Souqna = new(
        Name: "سوقنا للتجارة الإلكترونية",
        Slug: "souqna",
        Language: "ar",
        Timezone: "Asia/Riyadh",
        WeekStartsOnSunday: true,
        BrandColor: "#00796b",
        WelcomeText: "أهلًا بك في مركز مساعدة سوقنا! ابحث في المقالات أدناه، أو أرسل لنا طلبًا وسيرد عليك فريقنا خلال يوم عمل.",
        People:
        [
            new("owner", "ليلى الحربي", "layla@souqna.demo", Role.Owner),
            new("omar", "عمر الزهراني", "omar@souqna.demo", Role.Admin),
            new("noor", "نور القحطاني", "noor@souqna.demo", Role.Agent),
            new("khaled", "خالد العتيبي", "khaled@souqna.demo", Role.Agent),
            new("sara", "سارة الشمري", "sara@souqna.demo", Role.Agent),
        ],
        Teams:
        [
            new("orders", "الطلبات والشحن", "تتبع الطلبات والتوصيل والاستبدال.", ["noor", "khaled"]),
            new("payments", "المدفوعات", "الدفع والاسترداد والفواتير.", ["sara", "khaled"]),
        ],
        Topics:
        [
            new("orders", TicketType.Problem, ["شحن", "تأخير"],
                ["لم يصل طلبي حتى الآن", "تأخر توصيل الطلب رقم 48213", "شركة الشحن لا ترد على اتصالاتي"],
                "السلام عليكم،\n\nطلبت قبل أسبوع ولم يصلني الطلب حتى الآن، وحالة الشحنة لم تتغير منذ ثلاثة أيام.",
                "وعليكم السلام، نعتذر عن التأخير. تواصلنا مع شركة الشحن وستصلك الشحنة خلال يومين، وأرسلنا لك رابط التتبع المحدّث.",
                "شكرًا، هل يمكن التوصيل في المساء؟",
                "تم، طلبنا من المندوب التوصيل بعد الساعة الخامسة مساءً. نغلق الطلب الآن ويسعدنا تواصلك في أي وقت.",
                "الشحنة عالقة في مستودع الدمام، تم التصعيد لشركة الشحن."),
            new("orders", TicketType.Task, ["استبدال"],
                ["أريد استبدال المقاس", "وصلني منتج مختلف عن طلبي", "المنتج وصل تالفًا"],
                "مرحبًا،\n\nوصلني الطلب لكن المقاس غير مناسب، كيف أستبدله؟",
                "أهلًا بك! أنشأنا لك طلب استبدال مجاني، وسيأتي المندوب لاستلام المنتج وتسليمك المقاس الجديد.",
                "متى سيأتي المندوب؟",
                "سيصلك المندوب غدًا بين التاسعة صباحًا والواحدة ظهرًا. شكرًا لتسوقك من سوقنا."),
            new("payments", TicketType.Question, ["استرداد", "دفع"],
                ["متى يصل المبلغ المسترد؟", "تم خصم المبلغ مرتين", "الدفع عند الاستلام غير متاح"],
                "السلام عليكم،\n\nألغيت طلبي قبل خمسة أيام ولم يصلني المبلغ المسترد حتى الآن.",
                "وعليكم السلام، تم تنفيذ الاسترداد من طرفنا، ويستغرق ظهوره في حسابك من ثلاثة إلى سبعة أيام عمل حسب البنك.",
                "وصلني المبلغ اليوم، شكرًا لكم.",
                "يسعدنا ذلك! نغلق الطلب الآن.",
                "تم التحقق من بوابة الدفع: الاسترداد ناجح بتاريخ اليوم."),
            new("payments", TicketType.Question, ["فاتورة"],
                ["أحتاج فاتورة ضريبية للطلب", "الرقم الضريبي غير صحيح في الفاتورة"],
                "مرحبًا،\n\nأحتاج فاتورة ضريبية باسم الشركة لطلبي الأخير.",
                "أهلًا بك، أرسلنا الفاتورة الضريبية إلى بريدك الإلكتروني باسم الشركة والرقم الضريبي.",
                "وصلت، شكرًا.",
                "العفو، يسعدنا خدمتك."),
        ],
        Categories:
        [
            ("الطلبات والشحن", "تتبع طلبك ومواعيد التوصيل والاستبدال."),
            ("الدفع والاسترداد", "طرق الدفع والاسترداد والفواتير."),
        ],
        Articles:
        [
            new("الطلبات والشحن", "كيف أتتبع طلبي؟",
                "## تتبع الطلب\n\n1. افتح **طلباتي**.\n2. اختر الطلب.\n3. اضغط **تتبع الشحنة** لرؤية آخر تحديث من شركة الشحن.", 520, 44, 3),
            new("الطلبات والشحن", "مدة التوصيل",
                "يصل الطلب داخل المدن الرئيسية خلال يومين إلى ثلاثة أيام عمل، وخارجها خلال خمسة أيام عمل.", 384, 31, 5),
            new("الطلبات والشحن", "سياسة الاستبدال والإرجاع",
                "يمكنك استبدال المنتج أو إرجاعه خلال **14 يومًا** من الاستلام بشرط أن يكون بحالته الأصلية.", 290, 26, 2),
            new("الدفع والاسترداد", "طرق الدفع المتاحة",
                "نقبل مدى وفيزا وماستركارد وآبل باي والدفع عند الاستلام داخل المدن الرئيسية.", 330, 29, 1),
            new("الدفع والاسترداد", "متى يصل المبلغ المسترد؟",
                "يستغرق ظهور المبلغ في حسابك من 3 إلى 7 أيام عمل بعد تنفيذ الاسترداد، حسب البنك.", 410, 35, 6),
        ],
        Holidays: [(65, "اليوم الوطني")],
        CustomerNames:
        [
            "أحمد السالم", "فاطمة العلي", "محمد الغامدي", "نورة الدوسري", "عبدالله المطيري", "ريم القرني", "سلطان الحربي",
            "هيا العنزي", "يوسف الشهري", "منى الزهراني", "تركي البقمي", "جواهر السبيعي", "فهد العمري", "لمى الرشيد",
            "بندر الشمري", "دانة الخالدي", "ماجد الصاعدي", "أمل المالكي", "راشد التميمي", "شهد الجهني",
        ],
        CustomerDomain: "customers.demo",
        PortalCustomerEmail: "ahmed@customers.demo",
        PortalCustomerName: "أحمد السالم",
        PlanChanges: [(9, "pro")],
        TicketsPerDay: 1.7);

    /// <summary>Smaller workspaces that make the platform console's charts look like a real business.</summary>
    public static readonly (string Name, string Slug, int DaysAgo, string Plan, bool Suspended)[] Background =
    [
        ("Northwind Traders", "northwind-demo", 175, "business", false),
        ("Lumen Health", "lumen-demo", 150, "pro", false),
        ("Kestrel Logistics", "kestrel-demo", 128, "pro", true),
        ("Pinecone Labs", "pinecone-demo", 96, "free", false),
        ("Atlas Learning", "atlas-demo", 70, "pro", false),
        ("Harbor Coffee", "harbor-demo", 44, "free", false),
        ("Vela Studios", "vela-demo", 21, "pro", false),
        ("Juniper Legal", "juniper-demo", 6, "free", false),
    ];
}
