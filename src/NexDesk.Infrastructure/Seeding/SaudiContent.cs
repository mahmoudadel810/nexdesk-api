using System.Globalization;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tickets;

namespace NexDesk.Infrastructure.Seeding;

/// <summary>A text in Arabic and in English.</summary>
internal sealed record Bi(string Ar, string En)
{
    public string In(bool arabic) => arabic ? Ar : En;
}

internal sealed record SaudiTopic(
    string Team,
    TicketType Type,
    string[] Tags,
    Bi[] Subjects,
    Bi Description,
    Bi Reply,
    Bi FollowUp,
    Bi Closing,
    Bi? Note = null);

internal sealed record SaudiIndustry(
    string Key,
    Bi Noun,
    Bi Welcome,
    (string Key, Bi Name, Bi Description)[] Teams,
    SaudiTopic[] Topics,
    (Bi Name, Bi Description)[] Categories,
    (int Category, Bi Title, Bi Body)[] Articles,
    (string Key, Bi Label) Reference,
    Func<Random, string> ReferenceValue);

/// <summary>
/// Random but repeatable Saudi companies for a fuller demo: Saudi names, cities, mobile numbers, riyal amounts and
/// holidays, in Arabic or English, across ten made-up industries. Every company name is invented from common words.
/// </summary>
internal static class SaudiContent
{
    private static Bi T(string ar, string en) => new(ar, en);

    private static readonly (string Ar, string En)[] MaleNames =
    [
        ("محمد", "Mohammed"), ("عبدالله", "Abdullah"), ("فهد", "Fahad"), ("خالد", "Khaled"), ("سعود", "Saud"),
        ("فيصل", "Faisal"), ("تركي", "Turki"), ("نايف", "Nayef"), ("سلطان", "Sultan"), ("عبدالرحمن", "Abdulrahman"),
        ("ماجد", "Majed"), ("بندر", "Bandar"), ("يزيد", "Yazeed"), ("راكان", "Rakan"), ("عمر", "Omar"),
        ("أحمد", "Ahmed"), ("ياسر", "Yasser"), ("مشاري", "Mishari"), ("حمد", "Hamad"), ("زياد", "Ziyad"),
        ("عبدالعزيز", "Abdulaziz"), ("مازن", "Mazen"), ("هشام", "Hisham"), ("وليد", "Waleed"), ("نواف", "Nawaf"),
    ];

    private static readonly (string Ar, string En)[] FemaleNames =
    [
        ("نورة", "Noura"), ("سارة", "Sarah"), ("ريم", "Reem"), ("هيفاء", "Haifa"), ("لمى", "Lama"),
        ("جواهر", "Jawaher"), ("العنود", "Alanoud"), ("شهد", "Shahad"), ("دانة", "Dana"), ("منيرة", "Munira"),
        ("أمل", "Amal"), ("رهف", "Rahaf"), ("لجين", "Lujain"), ("غادة", "Ghada"), ("هند", "Hind"),
        ("مها", "Maha"), ("وعد", "Waad"), ("بشاير", "Bashayer"), ("أسيل", "Aseel"), ("روان", "Rawan"),
        ("نوف", "Nouf"), ("الجوهرة", "Aljawhara"), ("ديما", "Dima"), ("تهاني", "Tahani"), ("عبير", "Abeer"),
    ];

    private static readonly (string Ar, string En)[] FamilyNames =
    [
        ("القحطاني", "Al-Qahtani"), ("العتيبي", "Al-Otaibi"), ("الشهري", "Al-Shehri"), ("الغامدي", "Al-Ghamdi"),
        ("الدوسري", "Al-Dosari"), ("الحربي", "Al-Harbi"), ("الزهراني", "Al-Zahrani"), ("المطيري", "Al-Mutairi"),
        ("الشمري", "Al-Shammari"), ("العنزي", "Al-Anazi"), ("السبيعي", "Al-Subaie"), ("الرشيد", "Al-Rasheed"),
        ("البقمي", "Al-Buqami"), ("الجهني", "Al-Juhani"), ("الأحمدي", "Al-Ahmadi"), ("العمري", "Al-Omari"),
        ("المالكي", "Al-Maliki"), ("الثبيتي", "Al-Thubaiti"), ("باوزير", "Bawazir"), ("الخالدي", "Al-Khaldi"),
        ("السلمي", "Al-Sulami"), ("الفيفي", "Al-Faifi"), ("اليامي", "Al-Yami"), ("العسيري", "Al-Asiri"),
        ("الشهراني", "Al-Shahrani"), ("التميمي", "Al-Tamimi"), ("الهاجري", "Al-Hajri"), ("القرني", "Al-Qarni"),
    ];

    public static readonly (string Ar, string En)[] Cities =
    [
        ("الرياض", "Riyadh"), ("جدة", "Jeddah"), ("مكة المكرمة", "Makkah"), ("المدينة المنورة", "Madinah"),
        ("الدمام", "Dammam"), ("الخبر", "Khobar"), ("الطائف", "Taif"), ("تبوك", "Tabuk"), ("أبها", "Abha"),
        ("بريدة", "Buraydah"), ("حائل", "Hail"), ("جازان", "Jazan"), ("نجران", "Najran"), ("الأحساء", "Al-Ahsa"),
        ("ينبع", "Yanbu"), ("خميس مشيط", "Khamis Mushait"), ("الجبيل", "Jubail"), ("القطيف", "Qatif"),
    ];

    private static readonly (string Ar, string En)[] Prefixes =
    [
        ("سدرة", "Sidra"), ("واحة", "Waha"), ("رمال", "Rimal"), ("مرجان", "Murjan"), ("لؤلؤة", "Lulua"),
        ("غيم", "Ghaym"), ("أفق", "Ofuq"), ("سنام", "Sanam"), ("طويق", "Tuwaiq"), ("نجمة", "Najma"),
        ("روافد", "Rawafed"), ("زمرد", "Zumurrud"), ("قمرة", "Qamra"), ("ريحان", "Raihan"), ("سحاب", "Sahab"),
        ("وادي", "Wadi"), ("نسيم", "Naseem"), ("رواسي", "Rawasi"), ("ضياء", "Diyaa"), ("هضبة", "Hadba"),
        ("مدار", "Madar"), ("شروق", "Shurooq"), ("عرين", "Areen"), ("ندى", "Nada"), ("جسور", "Jusoor"),
        ("بيرق", "Bairaq"), ("أصيل", "Aseel"), ("خزامى", "Khuzama"), ("صفوة", "Safwa"), ("أريج", "Areej"),
    ];

    private static readonly string[] BrandColors =
        ["#006c35", "#00796b", "#1e6091", "#6a1b9a", "#ad1457", "#c62828", "#ef6c00", "#2e7d32", "#283593", "#00838f", "#4e342e", "#5d4037"];

    private static readonly string[] MailDomains = ["mail.demo", "inbox.demo", "email.demo", "post.demo"];

    private static readonly string[] MobilePrefixes = ["50", "53", "54", "55", "56", "57", "58", "59"];

    public static string Mobile(Random random) =>
        $"+966 {MobilePrefixes[random.Next(MobilePrefixes.Length)]} {random.Next(100, 1000)} {random.Next(1000, 10000)}";

    public static string Riyals(Random random, int min, int max) =>
        (random.Next(min, max) + (random.Next(0, 4) * 0.25)).ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>
    /// Generates <paramref name="count"/> workspaces. Histories end today and reach back 60 to 180 days; holidays are
    /// the Saudi ones that fall inside each history.
    /// </summary>
    public static DemoWorkspace[] Generate(int count, int seed, DateTimeOffset now)
    {
        var random = new Random(seed);
        var prefixes = Prefixes.OrderBy(_ => random.Next()).ToList();
        var workspaces = new List<DemoWorkspace>();
        for (var index = 0; index < count; index++)
        {
            var industry = Industries[index % Industries.Length];
            var prefix = prefixes[index % prefixes.Count];
            var arabic = random.NextDouble() < 0.7;
            var days = random.Next(60, 181);
            workspaces.Add(Build(industry, prefix, arabic, days, index, random, now));
        }

        return [.. workspaces];
    }

    private static DemoWorkspace Build(
        SaudiIndustry industry,
        (string Ar, string En) prefix,
        bool arabic,
        int days,
        int index,
        Random random,
        DateTimeOffset now)
    {
        var slug = $"{prefix.En.ToLowerInvariant()}-{industry.Key}";
        var name = arabic ? $"{prefix.Ar} {industry.Noun.Ar}" : $"{prefix.En} {industry.Noun.En}";
        var domain = $"{slug}.demo";
        var used = new HashSet<string>();

        DemoPerson Person(string key, Role role)
        {
            while (true)
            {
                var female = random.NextDouble() < 0.45;
                var pool = female ? FemaleNames : MaleNames;
                var first = pool[random.Next(pool.Length)];
                var family = FamilyNames[random.Next(FamilyNames.Length)];
                var email = $"{first.En.ToLowerInvariant()}.{family.En.ToLowerInvariant().Replace("al-", string.Empty)}@{domain}";
                if (used.Add(email))
                {
                    return new DemoPerson(key, arabic ? $"{first.Ar} {family.Ar}" : $"{first.En} {family.En}", email, role);
                }
            }
        }

        var people = new List<DemoPerson> { Person("owner", Role.Owner) };
        if (random.NextDouble() < 0.8)
        {
            people.Add(Person("admin", Role.Admin));
        }

        var agentCount = random.Next(3, 8);
        for (var a = 1; a <= agentCount; a++)
        {
            people.Add(Person($"agent{a}", Role.Agent));
        }

        if (random.NextDouble() < 0.5)
        {
            people.Add(Person("viewer", Role.Viewer));
        }

        var agents = people.Where(p => p.Role == Role.Agent).Select(p => p.Key).ToArray();
        var teams = industry.Teams.Select((team, t) =>
        {
            var members = agents.Where((_, a) => a % industry.Teams.Length == t || random.NextDouble() < 0.25).ToArray();
            return new DemoTeam(team.Key, team.Name.In(arabic), team.Description.In(arabic), members.Length > 0 ? members : [agents[t % agents.Length]]);
        }).ToArray();

        var topics = industry.Topics.Select(topic => new DemoTopic(
            topic.Team,
            topic.Type,
            topic.Tags,
            topic.Subjects.Select(s => s.In(arabic)).ToArray(),
            topic.Description.In(arabic),
            topic.Reply.In(arabic),
            topic.FollowUp.In(arabic),
            topic.Closing.In(arabic),
            topic.Note?.In(arabic))).ToArray();

        var customerCount = random.Next(40, 121);
        var customerNames = new string[customerCount];
        var customerEmails = new string[customerCount];
        for (var c = 0; c < customerCount; c++)
        {
            var female = random.NextDouble() < 0.45;
            var pool = female ? FemaleNames : MaleNames;
            var first = pool[random.Next(pool.Length)];
            var family = FamilyNames[random.Next(FamilyNames.Length)];
            customerNames[c] = arabic ? $"{first.Ar} {family.Ar}" : $"{first.En} {family.En}";
            customerEmails[c] =
                $"{first.En.ToLowerInvariant()}.{family.En.ToLowerInvariant().Replace("al-", string.Empty)}{c + 1}@{MailDomains[random.Next(MailDomains.Length)]}";
        }

        var start = now.AddDays(-days);
        var holidays = SaudiHolidays(arabic)
            .Select(h => ((int)(h.Date.ToDateTime(TimeOnly.MinValue) - start.UtcDateTime.Date).TotalDays, h.Name))
            .Where(h => h.Item1 >= 0 && h.Item1 < days)
            .ToArray();

        var planChanges = new List<(int, string)>();
        var plan = random.NextDouble();
        if (plan < 0.75)
        {
            planChanges.Add((random.Next(1, Math.Min(25, days / 2)), "pro"));
        }

        if (plan < 0.3)
        {
            planChanges.Add((random.Next(days / 2, days - 5), "business"));
        }

        var fields = new[]
        {
            new DemoField("mobile", arabic ? "رقم الجوال" : "Mobile number", CustomFieldType.Text, null, "mobile"),
            new DemoField("city", arabic ? "المدينة" : "City", CustomFieldType.Dropdown,
                Cities.Select(city => arabic ? city.Ar : city.En).ToArray(), "city"),
            new DemoField(industry.Reference.Key, industry.Reference.Label.In(arabic), CustomFieldType.Text, null, "ref"),
        };

        var portalFirst = (random.NextDouble() < 0.5 ? FemaleNames : MaleNames)[random.Next(20)];
        var portalFamily = FamilyNames[random.Next(FamilyNames.Length)];

        return new DemoWorkspace(
            Name: name,
            Slug: slug,
            Language: arabic ? "ar" : "en",
            Timezone: "Asia/Riyadh",
            WeekStartsOnSunday: true,
            BrandColor: BrandColors[random.Next(BrandColors.Length)],
            WelcomeText: industry.Welcome.In(arabic).Replace("{company}", name, StringComparison.Ordinal),
            People: [.. people],
            Teams: teams,
            Topics: topics,
            Categories: industry.Categories.Select(c => (c.Name.In(arabic), c.Description.In(arabic))).ToArray(),
            Articles: industry.Articles.Select(a => new DemoArticle(
                industry.Categories[a.Category].Name.In(arabic), a.Title.In(arabic), a.Body.In(arabic),
                random.Next(40, 900), random.Next(5, 80), random.Next(0, 9))).ToArray(),
            Holidays: holidays,
            CustomerNames: customerNames,
            CustomerDomain: MailDomains[0],
            PortalCustomerEmail: $"{portalFirst.En.ToLowerInvariant()}.{index + 1}@{domain.Replace(".demo", "-customer.demo", StringComparison.Ordinal)}",
            PortalCustomerName: arabic ? $"{portalFirst.Ar} {portalFamily.Ar}" : $"{portalFirst.En} {portalFamily.En}",
            PlanChanges: [.. planChanges],
            TicketsPerDay: 0.75 + (random.NextDouble() * 2.25))
        {
            Days = days,
            Fields = fields,
            CustomerEmails = customerEmails,
            Extras = true,
            Variables = r => Variables(r, arabic, industry),
        };
    }

    private static Dictionary<string, string> Variables(Random random, bool arabic, SaudiIndustry industry)
    {
        var city = Cities[random.Next(Cities.Length)];
        return new Dictionary<string, string>
        {
            ["city"] = arabic ? city.Ar : city.En,
            ["mobile"] = Mobile(random),
            ["ref"] = industry.ReferenceValue(random),
            ["amount"] = Riyals(random, 49, 4800),
        };
    }

    /// <summary>Public holidays in Saudi Arabia for 2026, with Eid dates as announced in advance.</summary>
    private static IEnumerable<(DateOnly Date, string Name)> SaudiHolidays(bool arabic)
    {
        (DateOnly, string Ar, string En)[] days =
        [
            (new DateOnly(2026, 2, 22), "يوم التأسيس", "Founding Day"),
            (new DateOnly(2026, 3, 19), "عطلة عيد الفطر", "Eid al-Fitr holiday"),
            (new DateOnly(2026, 3, 22), "عطلة عيد الفطر", "Eid al-Fitr holiday"),
            (new DateOnly(2026, 3, 23), "عطلة عيد الفطر", "Eid al-Fitr holiday"),
            (new DateOnly(2026, 5, 26), "يوم عرفة", "Arafat Day"),
            (new DateOnly(2026, 5, 27), "عطلة عيد الأضحى", "Eid al-Adha holiday"),
            (new DateOnly(2026, 5, 28), "عطلة عيد الأضحى", "Eid al-Adha holiday"),
            (new DateOnly(2026, 5, 31), "عطلة عيد الأضحى", "Eid al-Adha holiday"),
            (new DateOnly(2026, 9, 23), "اليوم الوطني", "Saudi National Day"),
        ];
        return days.Select(d => (d.Item1, arabic ? d.Ar : d.En));
    }

    private static string Digits(Random random, int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => random.Next(10).ToString(CultureInfo.InvariantCulture)));

    private static readonly SaudiIndustry[] Industries =
    [
        new(
            "store",
            T("للتجارة الإلكترونية", "Store"),
            T("أهلًا بك في مركز مساعدة {company}. تتبّع طلبك أو ابحث في الأدلة، وفريقنا يرد خلال ساعات العمل من الأحد إلى الخميس.",
              "Welcome to the {company} help centre. Track your order or browse our guides; our team replies Sunday to Thursday."),
            [
                ("payments", T("المدفوعات والاسترجاع", "Payments and refunds"), T("مدى، أبل باي، الاسترجاع والفواتير الضريبية.", "mada, Apple Pay, refunds and VAT invoices.")),
                ("orders", T("الطلبات والشحن", "Orders and delivery"), T("حالة الطلب والتوصيل لكل مدن المملكة.", "Order status and delivery across the Kingdom.")),
                ("care", T("خدمة العملاء", "Customer care"), T("الاستبدال والمقاسات والاستفسارات العامة.", "Exchanges, sizes and general questions.")),
            ],
            [
                new("orders", TicketType.Problem, ["delivery", "late"],
                    [T("تأخر توصيل الطلب {ref}", "Order {ref} is late"), T("الطلب ما وصل إلى {city}", "My order hasn't reached {city}"), T("متى يوصل طلبي؟", "When will my order arrive?")],
                    T("السلام عليكم،\n\nطلبت قبل خمسة أيام ورقم الطلب {ref} ولا زال الطلب ما وصل إلى {city}. رقم جوالي {mobile}.\n\nأرجو الإفادة.",
                      "Hello,\n\nI placed order {ref} five days ago and it still hasn't arrived in {city}. My mobile is {mobile}.\n\nCould you check?"),
                    T("وعليكم السلام، نعتذر عن التأخير. تواصلنا مع شركة الشحن والطلب حاليًا في مستودع {city} وسيصلك خلال يومي عمل.",
                      "Sorry for the delay. We've checked with the courier: the parcel is at the {city} hub and will reach you within two working days."),
                    T("وصلني اتصال من المندوب، شكرًا لكم.", "The courier just called me, thank you."),
                    T("يسعدنا ذلك! نغلق الطلب الآن، ولا تتردد بالتواصل معنا.", "Great to hear! We'll close this now; reach out any time."),
                    T("الشحنة متأخرة بسبب ضغط مستودع {city}. @{teammate} للمتابعة مع شركة الشحن.", "Delay caused by the {city} hub backlog. @{teammate} please follow up with the courier.")),
                new("payments", TicketType.Question, ["refund"],
                    [T("استرجاع مبلغ طلب ملغي", "Refund for a cancelled order"), T("المبلغ ما رجع للبطاقة", "Refund not back on my card"), T("استرداد قيمة منتج مرتجع", "Refund for a returned item")],
                    T("مرحبًا،\n\nألغيت الطلب {ref} قبل أسبوع والمبلغ {amount} ريال ما رجع لبطاقة مدى إلى الآن.",
                      "Hi,\n\nI cancelled order {ref} a week ago and the {amount} SAR still hasn't come back to my mada card."),
                    T("أهلًا بك، تم تنفيذ الاسترجاع من طرفنا بتاريخ اليوم، ويظهر في حسابك خلال 3 إلى 7 أيام عمل حسب البنك.",
                      "We've issued the refund today. Depending on your bank it shows in your account within 3 to 7 working days."),
                    T("وصل المبلغ، شكرًا.", "The money arrived, thanks."),
                    T("ممتاز، نسعد بخدمتك دائمًا.", "Perfect, always happy to help."),
                    T("الاسترجاع عبر بوابة الدفع، مرجع العملية RF-{ref}.", "Refund issued through the gateway, reference RF-{ref}.")),
                new("payments", TicketType.Task, ["vat", "invoice"],
                    [T("طلب فاتورة ضريبية باسم المنشأة", "VAT invoice in our company name"), T("الرقم الضريبي غير موجود في الفاتورة", "VAT number missing from invoice")],
                    T("السلام عليكم،\n\nنحتاج فاتورة ضريبية للطلب {ref} باسم المنشأة مع الرقم الضريبي، لأن الفاتورة الحالية باسم شخصي.",
                      "Hello,\n\nWe need a VAT invoice for order {ref} in our company name with our VAT number; the current one is in a personal name."),
                    T("تم إصدار فاتورة ضريبية جديدة بالبيانات المرسلة وأرسلناها إلى بريدك.", "We've issued a new VAT invoice with your details and emailed it to you."),
                    T("استلمتها، هل تصدر الفواتير القادمة بنفس البيانات؟", "Got it. Will future invoices use these details?"),
                    T("نعم، حفظنا بيانات المنشأة في حسابك لكل الطلبات القادمة.", "Yes, your company details are saved for every future order.")),
                new("care", TicketType.Question, ["exchange"],
                    [T("استبدال مقاس", "Exchange for another size"), T("وصلني منتج غير المطلوب", "I received the wrong item"), T("المنتج وصل تالف", "Item arrived damaged")],
                    T("مرحبًا،\n\nوصلني الطلب {ref} لكن المقاس غير مناسب، أبغى أستبدله بمقاس أكبر.",
                      "Hi,\n\nOrder {ref} arrived but the size is wrong. I'd like to swap it for a larger one."),
                    T("أكيد! أرسلنا لك رابط الإرجاع، والمندوب يستلم المنتج من عنوانك في {city} ويوصلك المقاس الجديد.",
                      "Of course! We've sent you a return link; the courier will collect it from your address in {city} and deliver the new size."),
                    T("تمام، متى يجي المندوب؟", "Great, when will the courier come?"),
                    T("تم الاستبدال بنجاح ووصلك المقاس الجديد. شكرًا لثقتك.", "The exchange is complete and the new size has been delivered. Thank you!")),
            ],
            [(T("الطلبات والتوصيل", "Orders and delivery"), T("تتبع الطلبات ومدد التوصيل.", "Tracking orders and delivery times.")),
             (T("الدفع والاسترجاع", "Payment and refunds"), T("طرق الدفع ومدة الاسترجاع.", "Payment methods and refund times."))],
            [
                (0, T("كم مدة التوصيل؟", "How long does delivery take?"),
                    T("## مدة التوصيل\n\n- **الرياض وجدة والدمام:** من يوم إلى يومين عمل.\n- **باقي المدن:** من 3 إلى 5 أيام عمل.\n\nتصلك رسالة نصية فيها رابط التتبع فور شحن الطلب.",
                      "## Delivery times\n\n- **Riyadh, Jeddah and Dammam:** 1 to 2 working days.\n- **Other cities:** 3 to 5 working days.\n\nYou get a text with a tracking link as soon as your order ships.")),
                (1, T("متى يرجع المبلغ بعد الاسترجاع؟", "When will my refund arrive?"),
                    T("نعيد المبلغ لنفس وسيلة الدفع خلال يوم عمل من استلام المرتجع. **مدى:** 3 إلى 7 أيام عمل. **أبل باي والبطاقات الائتمانية:** حتى 14 يومًا.",
                      "We refund to the original payment method within a working day of receiving the return. **mada:** 3 to 7 working days. **Apple Pay and credit cards:** up to 14 days.")),
                (1, T("طرق الدفع المتاحة", "Payment methods we accept"),
                    T("نقبل مدى، فيزا، ماستركارد، أبل باي، والدفع عند الاستلام بحد أقصى 1,500 ريال.",
                      "We accept mada, Visa, Mastercard, Apple Pay and cash on delivery up to SAR 1,500.")),
            ],
            ("order_number", T("رقم الطلب", "Order number")),
            r => $"#{r.Next(100000, 999999)}"),

        new(
            "telecom",
            T("للاتصالات", "Telecom"),
            T("مرحبًا بك في دعم {company}. اطّلع على أدلة الألياف والفواتير أو افتح طلبًا وسنخدمك بأسرع وقت.",
              "Welcome to {company} support. Browse our fibre and billing guides, or open a request and we'll help right away."),
            [
                ("payments", T("الفواتير", "Billing"), T("الفواتير والباقات والمدفوعات.", "Bills, packages and payments.")),
                ("network", T("الدعم الفني", "Technical support"), T("الألياف البصرية والراوتر وسرعة الإنترنت.", "Fibre, routers and internet speed.")),
                ("sales", T("المبيعات", "Sales"), T("الاشتراكات الجديدة ونقل الأرقام.", "New subscriptions and number transfers.")),
            ],
            [
                new("network", TicketType.Incident, ["fiber", "slow"],
                    [T("الإنترنت بطيء جدًا", "Internet is very slow"), T("انقطاع الألياف في حي بـ{city}", "Fibre outage in my area of {city}"), T("الراوتر يفصل كل شوي", "Router keeps disconnecting")],
                    T("السلام عليكم،\n\nالإنترنت عندي في {city} بطيء من أمس، السرعة ما تتعدى 5 ميقا والباقة 200 ميقا. رقم الخدمة {ref}.",
                      "Hello,\n\nMy internet in {city} has been slow since yesterday: under 5 Mbps on a 200 Mbps plan. Service number {ref}."),
                    T("نعتذر منك. فحصنا الخط عن بُعد ووجدنا ضعفًا في الإشارة، أعدنا ضبط المنفذ. هل تحسنت السرعة؟",
                      "Sorry about that. We tested the line remotely, found a weak signal and reset your port. Is it better now?"),
                    T("تحسنت شوي بس لا زالت أقل من المعتاد.", "A little better, but still slower than usual."),
                    T("زار الفني موقعك وغيّر كابل الألياف، والسرعة الآن مطابقة للباقة. شكرًا لصبرك.", "Our technician replaced the fibre cable on site and your speed now matches the plan. Thanks for your patience."),
                    T("حجزت زيارة فني لحي العميل. @{teammate} تأكد من توفر كابل احتياطي.", "Booked a technician visit. @{teammate} please make sure a spare cable is on the van.")),
                new("payments", TicketType.Question, ["bill", "invoice"],
                    [T("الفاتورة أعلى من المعتاد", "My bill is higher than usual"), T("استفسار عن فاتورة الشهر", "Question about this month's invoice")],
                    T("مرحبًا،\n\nفاتورة هذا الشهر {amount} ريال وأنا مشترك في باقة ثابتة. ممكن توضحون السبب؟",
                      "Hi,\n\nThis month's bill is {amount} SAR, but I'm on a fixed package. Can you explain why?"),
                    T("راجعنا الفاتورة، والزيادة بسبب تجوال دولي خلال سفرك الأخير. أرسلنا لك تفاصيل الاستخدام.",
                      "We've reviewed the bill: the extra comes from international roaming on your recent trip. We've sent you the usage details."),
                    T("فهمت، هل فيه باقة تجوال أوفر للمرة الجاية؟", "I see. Is there a cheaper roaming bundle for next time?"),
                    T("نعم، فعّلنا لك عرض التجوال الخليجي بخصم 30٪ على رحلتك القادمة.", "Yes, we've added the GCC roaming offer with 30% off your next trip.")),
                new("sales", TicketType.Task, ["new-line"],
                    [T("طلب تركيب ألياف جديد", "New fibre installation"), T("نقل الاشتراك لعنوان جديد", "Move my subscription to a new address")],
                    T("السلام عليكم،\n\nانتقلت لبيت جديد في {city} وأبغى أنقل اشتراك الألياف. جوالي {mobile}.",
                      "Hello,\n\nI've moved to a new home in {city} and want to move my fibre subscription. My mobile is {mobile}."),
                    T("حياك الله! الحي مغطى بالألياف، وحجزنا لك موعد تركيب يوم الأحد بين 9 و12 صباحًا.",
                      "Welcome! Your area is covered and we've booked an installation for Sunday between 9 and 12."),
                    T("مناسب، شكرًا.", "That works, thanks."),
                    T("تم التركيب وتفعيل الخدمة في العنوان الجديد. مبروك البيت الجديد!", "Installed and active at your new address. Enjoy the new home!")),
                new("sales", TicketType.Question, ["esim"],
                    [T("تفعيل شريحة إلكترونية eSIM", "Activate an eSIM"), T("نقل رقمي من شركة أخرى", "Port my number from another provider")],
                    T("مرحبًا،\n\nأبغى أحوّل رقمي {mobile} إلى شريحة إلكترونية على جوالي الجديد.",
                      "Hi,\n\nI'd like to move my number {mobile} to an eSIM on my new phone."),
                    T("أكيد، أرسلنا لك رمز QR عبر البريد. تحتاج تثبيته من إعدادات الجوال بعد التحقق عبر نفاذ.",
                      "Sure. We've emailed you a QR code; scan it from your phone's settings after verifying your identity."),
                    T("تم التثبيت والشبكة شغالة.", "Installed and the network is working."),
                    T("ممتاز! نغلق الطلب الآن.", "Excellent! Closing this now.")),
            ],
            [(T("الإنترنت والألياف", "Internet and fibre"), T("حل مشاكل السرعة والانقطاع.", "Fixing speed and outage problems.")),
             (T("الفواتير والباقات", "Bills and packages"), T("فهم الفاتورة وتغيير الباقة.", "Understanding your bill and changing package."))],
            [
                (0, T("خطوات قبل التواصل مع الدعم عند بطء الإنترنت", "Before you contact us about slow internet"),
                    T("1. أعد تشغيل الراوتر وانتظر دقيقتين.\n2. جرّب الاتصال بكابل بدل الواي فاي.\n3. قِس السرعة من جهاز واحد فقط.\n\nإذا استمرت المشكلة افتح طلبًا وأرفق نتيجة قياس السرعة.",
                      "1. Restart the router and wait two minutes.\n2. Try a cable instead of Wi-Fi.\n3. Test the speed from one device only.\n\nStill slow? Open a request and attach your speed test.")),
                (1, T("كيف أقرأ فاتورتي؟", "How to read your bill"),
                    T("تتكون الفاتورة من **رسوم الباقة**، **الاستخدام الإضافي** و**ضريبة القيمة المضافة 15٪**.",
                      "Your bill has **package fees**, **extra usage** and **15% VAT**.")),
            ],
            ("service_number", T("رقم الخدمة", "Service number")),
            r => $"FTTH-{Digits(r, 8)}"),

        new(
            "delivery",
            T("للتوصيل", "Delivery"),
            T("أهلًا بك في {company}. شاركنا مشكلة طلبك وسنعوّضك بأسرع وقت.", "Welcome to {company}. Tell us what went wrong with your order and we'll make it right fast."),
            [
                ("payments", T("المدفوعات", "Payments"), T("الاسترجاع والمحفظة ومستحقات الشركاء.", "Refunds, wallet and partner payouts.")),
                ("ops", T("العمليات", "Operations"), T("المناديب ووقت التوصيل.", "Drivers and delivery times.")),
                ("partners", T("شركاء المطاعم", "Restaurant partners"), T("انضمام المطاعم وقوائم الطعام.", "Restaurant onboarding and menus.")),
            ],
            [
                new("ops", TicketType.Problem, ["late", "cold"],
                    [T("الطلب وصل بارد ومتأخر", "Order arrived late and cold"), T("المندوب ما وصل", "Driver never arrived")],
                    T("السلام عليكم،\n\nطلبي {ref} وصل بعد ساعة ونص والأكل بارد. أبغى تعويض.",
                      "Hello,\n\nMy order {ref} arrived 90 minutes late and the food was cold. I'd like compensation."),
                    T("نعتذر جدًا عن التجربة. أضفنا {amount} ريال رصيدًا في محفظتك تعويضًا عن الطلب.",
                      "We're very sorry. We've added {amount} SAR to your wallet for this order."),
                    T("شكرًا، وصل الرصيد.", "Thanks, the credit is there."),
                    T("نتمنى لك تجربة أفضل في طلبك القادم!", "We hope your next order is much better!"),
                    T("المندوب تأخر بسبب زحمة طريق الملك فهد. @{teammate} راجع توزيع الطلبات وقت الذروة.", "Driver delayed by traffic. @{teammate} please review peak-hour dispatch.")),
                new("payments", TicketType.Question, ["refund", "missing-item"],
                    [T("صنف ناقص من الطلب", "An item was missing"), T("استرجاع مبلغ صنف ناقص", "Refund for a missing item")],
                    T("مرحبًا،\n\nطلبت وجبتين ووصلت وحدة بس. رقم الطلب {ref}.", "Hi,\n\nI ordered two meals and only one arrived. Order {ref}."),
                    T("تم استرجاع قيمة الصنف الناقص {amount} ريال إلى وسيلة الدفع الأصلية.", "We've refunded the missing item, {amount} SAR, to your original payment method."),
                    T("تمام شكرًا.", "Okay, thank you."),
                    T("العفو، نغلق الطلب الآن.", "You're welcome, closing this now.")),
                new("partners", TicketType.Task, ["onboarding"],
                    [T("طلب انضمام مطعم جديد", "New restaurant wants to join"), T("تحديث قائمة الطعام", "Update our menu")],
                    T("السلام عليكم،\n\nعندنا مطعم مندي في {city} ونبغى ننضم للتطبيق. السجل التجاري جاهز.",
                      "Hello,\n\nWe run a mandi restaurant in {city} and want to join the app. Our commercial registration is ready."),
                    T("حياكم! أرسلنا لكم نموذج التسجيل، ونحتاج السجل التجاري وشهادة الزكاة والضريبة والقائمة مع الأسعار.",
                      "Welcome! We've sent you the sign-up form; we need your commercial registration, ZATCA certificate and menu with prices."),
                    T("أرسلنا كل المستندات.", "We've sent all the documents."),
                    T("تم تفعيل المطعم في التطبيق وصار ظاهرًا للعملاء في {city}. بالتوفيق!", "Your restaurant is live in the app for customers in {city}. Best of luck!")),
                new("payments", TicketType.Question, ["payout", "invoice"],
                    [T("مستحقات المطعم متأخرة", "Restaurant payout is late"), T("فاتورة العمولة الشهرية", "Monthly commission invoice")],
                    T("مرحبًا،\n\nمستحقات الأسبوع الماضي {amount} ريال ما وصلت حسابنا البنكي إلى الآن.",
                      "Hi,\n\nLast week's payout of {amount} SAR hasn't reached our bank account yet."),
                    T("تحققنا، والتحويل تم أمس عبر نظام سريع ويظهر خلال يوم عمل.", "We checked: the transfer went out yesterday and should show within a working day."),
                    T("وصل، شكرًا.", "Received, thanks."),
                    T("ممتاز، بالتوفيق.", "Great, all the best.")),
            ],
            [(T("طلباتي", "My orders"), T("التأخير والأصناف الناقصة.", "Late orders and missing items.")),
             (T("للشركاء", "For partners"), T("انضمام المطاعم والمستحقات.", "Restaurant onboarding and payouts."))],
            [
                (0, T("ماذا أفعل إذا تأخر طلبي؟", "What if my order is late?"),
                    T("تابع موقع المندوب من صفحة الطلب. إذا تجاوز التأخير 30 دقيقة عن الوقت المتوقع، افتح طلبًا وسنعوّضك برصيد في المحفظة.",
                      "Follow the driver from the order page. If it's more than 30 minutes past the estimate, open a request and we'll add wallet credit.")),
                (1, T("متى تُحوّل مستحقات المطاعم؟", "When are restaurant payouts sent?"),
                    T("نحوّل المستحقات أسبوعيًا كل يوم أحد، بعد خصم العمولة وضريبة القيمة المضافة.",
                      "Payouts go out weekly every Sunday, after commission and VAT.")),
            ],
            ("order_number", T("رقم الطلب", "Order number")),
            r => $"D-{r.Next(1000000, 9999999)}"),

        new(
            "clinics",
            T("الطبية", "Clinics"),
            T("مرحبًا بك في {company}. احجز موعدك أو اطلب تقريرك الطبي، ونحن في خدمتك.", "Welcome to {company}. Book an appointment or request a medical report; we're here to help."),
            [
                ("payments", T("المالية والتأمين", "Billing and insurance"), T("موافقات التأمين والفواتير.", "Insurance approvals and invoices.")),
                ("appointments", T("المواعيد", "Appointments"), T("الحجز والتعديل والإلغاء.", "Booking, rescheduling and cancelling.")),
                ("records", T("التقارير الطبية", "Medical records"), T("التقارير والإجازات المرضية.", "Reports and sick leave.")),
            ],
            [
                new("appointments", TicketType.Task, ["appointment"],
                    [T("تعديل موعد عيادة الأسنان", "Reschedule my dental appointment"), T("حجز موعد في فرع {city}", "Book an appointment at the {city} branch")],
                    T("السلام عليكم،\n\nعندي موعد يوم الثلاثاء وأبغى أأجله للأسبوع الجاي. رقم الملف {ref}، جوالي {mobile}.",
                      "Hello,\n\nI have an appointment on Tuesday and need to move it to next week. File {ref}, mobile {mobile}."),
                    T("أهلًا بك، المواعيد المتاحة الأسبوع القادم: الأحد 10 صباحًا أو الأربعاء 4 عصرًا. أيهما يناسبك؟",
                      "Available next week: Sunday 10 am or Wednesday 4 pm. Which suits you?"),
                    T("الأربعاء 4 العصر مناسب.", "Wednesday at 4 works."),
                    T("تم تأكيد موعدك يوم الأربعاء الساعة 4 عصرًا، وستصلك رسالة تذكير قبلها بيوم.", "Confirmed for Wednesday at 4 pm; you'll get a reminder the day before.")),
                new("payments", TicketType.Question, ["insurance"],
                    [T("موافقة التأمين متأخرة", "Insurance approval is taking long"), T("رفض التأمين للتحاليل", "Insurance rejected my lab tests")],
                    T("مرحبًا،\n\nالطبيب طلب أشعة رنين، والموافقة من شركة التأمين ما وصلت من ثلاثة أيام.",
                      "Hi,\n\nThe doctor ordered an MRI and the insurance approval still hasn't come after three days."),
                    T("تابعنا مع شركة التأمين، وطلبوا تقريرًا إضافيًا من الطبيب وأرسلناه اليوم.", "We followed up with your insurer; they asked for an extra report, which we sent today."),
                    T("أي تحديث؟", "Any update?"),
                    T("وصلت الموافقة وحجزنا لك موعد الأشعة. نتمنى لك الصحة والعافية.", "The approval came through and your MRI is booked. Wishing you good health."),
                    T("رقم المطالبة {ref}، @{teammate} تابع مع منسق التأمين.", "Claim {ref}; @{teammate} please chase the insurance coordinator.")),
                new("records", TicketType.Task, ["sick-leave", "report"],
                    [T("طلب تقرير طبي", "Request a medical report"), T("الإجازة المرضية ما ظهرت في صحتي", "Sick leave not showing in the health app")],
                    T("السلام عليكم،\n\nزرت العيادة أمس وأحتاج الإجازة المرضية لجهة عملي، وما ظهرت في التطبيق.",
                      "Hello,\n\nI visited yesterday and need my sick leave for my employer, but it isn't showing in the app."),
                    T("تم إصدار الإجازة وربطها برقم هويتك، وتظهر في التطبيق خلال ساعة.", "The sick leave is issued and linked to your ID; it will show in the app within an hour."),
                    T("ظهرت الآن، شكرًا.", "It's there now, thanks."),
                    T("الحمد لله على السلامة.", "Get well soon.")),
                new("payments", TicketType.Question, ["invoice"],
                    [T("نسخة من الفاتورة للتأمين", "Invoice copy for my insurer"), T("فاتورة الزيارة", "Invoice for my visit")],
                    T("مرحبًا،\n\nأحتاج فاتورة مفصلة لزيارتي بمبلغ {amount} ريال لاسترجاعها من التأمين.",
                      "Hi,\n\nI need an itemised invoice for my {amount} SAR visit to claim it from my insurer."),
                    T("أرسلنا الفاتورة المفصلة مع التقرير الطبي إلى بريدك.", "We've emailed you the itemised invoice and the medical report."),
                    T("وصلت، شكرًا.", "Received, thank you."),
                    T("بالعافية.", "Take care.")),
            ],
            [(T("المواعيد", "Appointments"), T("الحجز والتعديل.", "Booking and changes.")),
             (T("التأمين والفواتير", "Insurance and invoices"), T("الموافقات والمطالبات.", "Approvals and claims."))],
            [
                (0, T("كيف أعدّل موعدي؟", "How do I change my appointment?"),
                    T("من التطبيق: **مواعيدي** ثم **تعديل**. يمكن التعديل حتى 12 ساعة قبل الموعد دون رسوم.",
                      "In the app go to **My appointments**, then **Change**. Changes are free up to 12 hours before.")),
                (1, T("كم تستغرق موافقة التأمين؟", "How long do insurance approvals take?"),
                    T("أغلب الموافقات خلال 24 ساعة. الأشعة والعمليات قد تحتاج حتى 3 أيام عمل.",
                      "Most approvals take under 24 hours; scans and procedures can take up to 3 working days.")),
            ],
            ("file_number", T("رقم الملف الطبي", "Medical file number")),
            r => $"MRN-{Digits(r, 7)}"),

        new(
            "academy",
            T("للتدريب", "Academy"),
            T("أهلًا بك في {company}. ابحث في الأسئلة الشائعة عن الدورات والشهادات أو تواصل معنا.", "Welcome to {company}. Find answers about courses and certificates, or contact us."),
            [
                ("payments", T("المدفوعات", "Payments"), T("الرسوم والتقسيط وفواتير الجهات.", "Fees, instalments and corporate invoices.")),
                ("learning", T("دعم المتدربين", "Learner support"), T("الدخول للمنصة والمحتوى.", "Platform access and content.")),
                ("certs", T("الشهادات", "Certificates"), T("إصدار الشهادات وتصحيحها.", "Issuing and correcting certificates.")),
            ],
            [
                new("learning", TicketType.Problem, ["access"],
                    [T("ما أقدر أدخل الدورة", "I can't open my course"), T("الفيديوهات ما تشتغل", "Course videos won't play")],
                    T("السلام عليكم،\n\nسجلت في دورة إدارة المشاريع ودفعت، لكن الدورة ما ظهرت في حسابي.",
                      "Hello,\n\nI registered and paid for the project management course but it isn't in my account."),
                    T("نعتذر، كان التسجيل مرتبطًا ببريد آخر. نقلنا الدورة لحسابك الآن.", "Sorry, the enrolment was linked to another email. We've moved it to your account."),
                    T("ظهرت، شكرًا.", "It's showing now, thanks."),
                    T("بالتوفيق في دراستك!", "Good luck with your studies!")),
                new("certs", TicketType.Task, ["certificate"],
                    [T("الاسم في الشهادة خطأ", "Wrong name on my certificate"), T("أبغى الشهادة بالإنجليزي", "I need my certificate in English")],
                    T("مرحبًا،\n\nاسمي في الشهادة مكتوب غلط، أبغى يكون مطابقًا للهوية الوطنية.", "Hi,\n\nMy name is misspelled on the certificate; it should match my national ID."),
                    T("صححنا الاسم وأعدنا إصدار الشهادة برقم تحقق جديد {ref}.", "We've corrected the name and reissued the certificate with a new verification number {ref}."),
                    T("ممتاز، هل تظهر في منصة التحقق؟", "Great, does it show on the verification page?"),
                    T("نعم، يمكن لجهة عملك التحقق منها برقم الشهادة.", "Yes, your employer can verify it with the certificate number.")),
                new("payments", TicketType.Question, ["instalments"],
                    [T("الدفع بالتقسيط", "Paying in instalments"), T("سداد الرسوم على دفعات", "Splitting the course fee")],
                    T("السلام عليكم،\n\nرسوم الدبلوم {amount} ريال، هل يمكن تقسيطها على أربع دفعات؟", "Hello,\n\nThe diploma costs {amount} SAR. Can I pay it in four instalments?"),
                    T("نعم، فعّلنا لك خيار التقسيط على أربع دفعات بدون رسوم إضافية.", "Yes, we've enabled four interest-free instalments for you."),
                    T("تم الدفع الأول.", "First payment done."),
                    T("أهلًا بك معنا، تم تفعيل الدبلوم.", "Welcome aboard, your diploma is active.")),
                new("payments", TicketType.Task, ["invoice", "corporate"],
                    [T("فاتورة باسم جهة العمل", "Invoice for my employer"), T("عرض سعر لتدريب موظفين", "Quote for training our staff")],
                    T("مرحبًا،\n\nجهة عملي ستدفع الرسوم وتحتاج فاتورة ضريبية باسم الشركة.", "Hi,\n\nMy employer is paying the fee and needs a VAT invoice in the company's name."),
                    T("أصدرنا الفاتورة باسم الشركة ورقمها الضريبي وأرسلناها لك.", "We've issued the invoice to the company with its VAT number."),
                    T("وصلت، شكرًا.", "Got it, thanks."),
                    T("نسعد بخدمتكم.", "Happy to help.")),
            ],
            [(T("الدورات", "Courses"), T("الوصول للمحتوى.", "Accessing content.")),
             (T("الشهادات", "Certificates"), T("الإصدار والتحقق.", "Issuing and verification."))],
            [
                (0, T("الدورة ما ظهرت في حسابي", "My course isn't showing"), T("تأكد أنك مسجل بنفس البريد المستخدم في الدفع، ثم سجّل خروج ودخول.", "Make sure you signed in with the email you paid with, then sign out and back in.")),
                (1, T("كيف أتحقق من شهادتي؟", "How do I verify a certificate?"), T("أدخل رقم الشهادة في صفحة التحقق، وتظهر بيانات المتدرب والدورة.", "Enter the certificate number on the verification page to see the learner and course.")),
            ],
            ("learner_id", T("رقم المتدرب", "Learner ID")),
            r => $"LRN-{Digits(r, 6)}"),

        new(
            "logistics",
            T("للخدمات اللوجستية", "Logistics"),
            T("مرحبًا بك في {company}. تتبّع شحنتك أو اسأل عن التخليص الجمركي.", "Welcome to {company}. Track a shipment or ask about customs clearance."),
            [
                ("payments", T("التحصيل والفواتير", "Collections and invoices"), T("مبالغ الدفع عند الاستلام والفواتير.", "Cash on delivery remittances and invoices.")),
                ("shipping", T("الشحن", "Shipping"), T("التتبع والتوصيل والعناوين.", "Tracking, delivery and addresses.")),
                ("customs", T("التخليص الجمركي", "Customs"), T("الشحنات الدولية والرسوم الجمركية.", "International shipments and duties.")),
            ],
            [
                new("customs", TicketType.Problem, ["customs"],
                    [T("الشحنة عالقة في الجمارك", "Shipment held at customs"), T("رسوم جمركية غير متوقعة", "Unexpected customs duty")],
                    T("السلام عليكم،\n\nشحنتي {ref} موقوفة في الجمارك من أسبوع ويطلبون فاتورة الشراء.", "Hello,\n\nShipment {ref} has been held at customs for a week and they want the purchase invoice."),
                    T("أرفق فاتورة الشراء هنا ونكمل التخليص نيابة عنك.", "Please attach the purchase invoice here and we'll complete clearance for you."),
                    T("أرفقت الفاتورة.", "Invoice attached."),
                    T("تم التخليص والشحنة في طريقها إلى {city}.", "Cleared; the shipment is on its way to {city}."),
                    T("الجمارك تطلب شهادة مطابقة أيضًا. @{teammate} تواصل مع المخلص.", "Customs also want a conformity certificate. @{teammate} please contact the broker.")),
                new("shipping", TicketType.Task, ["address"],
                    [T("تحديث العنوان الوطني", "Update my national address"), T("تغيير عنوان التوصيل", "Change the delivery address")],
                    T("مرحبًا،\n\nالعنوان الوطني في الشحنة {ref} قديم، أبغى التوصيل لعنواني الجديد في {city}.", "Hi,\n\nShipment {ref} has my old national address; please deliver to my new one in {city}."),
                    T("حدّثنا العنوان، وسيتواصل معك المندوب على {mobile} قبل الوصول.", "Updated. The courier will call you on {mobile} before arriving."),
                    T("تمام.", "Great."),
                    T("تم التسليم بنجاح.", "Delivered successfully.")),
                new("payments", TicketType.Question, ["cod", "invoice"],
                    [T("تحويل مبالغ الدفع عند الاستلام", "Cash on delivery remittance"), T("فاتورة الشحن الشهرية", "Monthly shipping invoice")],
                    T("السلام عليكم،\n\nمتجرنا ينتظر تحويل مبالغ الدفع عند الاستلام {amount} ريال للأسبوع الماضي.", "Hello,\n\nOur store is waiting for last week's cash-on-delivery remittance of {amount} SAR."),
                    T("تمت مطابقة الشحنات وسيتم التحويل غدًا مع كشف الحساب.", "The shipments are reconciled; the transfer goes out tomorrow with a statement."),
                    T("وصل التحويل.", "Transfer received."),
                    T("شكرًا لتعاملكم معنا.", "Thanks for working with us.")),
                new("shipping", TicketType.Incident, ["damaged"],
                    [T("الشحنة وصلت تالفة", "Parcel arrived damaged"), T("الكرتون مفتوح", "Box arrived opened")],
                    T("مرحبًا،\n\nاستلمت الشحنة {ref} والكرتون مكسور والجهاز داخله متضرر.", "Hi,\n\nShipment {ref} arrived with a crushed box and the device inside is damaged."),
                    T("نعتذر جدًا. أرسل صور الكرتون والمنتج لنفتح مطالبة تعويض.", "We're very sorry. Please send photos of the box and item so we can open a claim."),
                    T("أرسلت الصور.", "Photos sent."),
                    T("تمت الموافقة على التعويض بمبلغ {amount} ريال.", "Your claim of {amount} SAR has been approved.")),
            ],
            [(T("التتبع والتوصيل", "Tracking and delivery"), T("حالة الشحنات.", "Shipment status.")),
             (T("الشحن الدولي", "International shipping"), T("الجمارك والرسوم.", "Customs and duties."))],
            [
                (0, T("لماذا أحتاج العنوان الوطني؟", "Why do you need my national address?"), T("العنوان الوطني يضمن وصول المندوب لموقعك بدقة. حدّثه من حسابك قبل الشحن.", "Your national address gets the courier to the right door. Update it in your account before shipping.")),
                (1, T("ما المستندات المطلوبة للتخليص؟", "Documents needed for customs"), T("فاتورة الشراء، وصورة الهوية أو الإقامة، وشهادة المطابقة لبعض المنتجات.", "The purchase invoice, a copy of your ID or iqama, and a conformity certificate for some goods.")),
            ],
            ("tracking_number", T("رقم الشحنة", "Tracking number")),
            r => $"SA{Digits(r, 10)}"),

        new(
            "pay",
            T("للمدفوعات الرقمية", "Pay"),
            T("مرحبًا بك في دعم {company}. نحن هنا لمساعدتك في التحويلات والبطاقات والتحقق من الهوية.", "Welcome to {company} support, for transfers, cards and identity checks."),
            [
                ("payments", T("الرسوم والكشوفات", "Fees and statements"), T("الرسوم والكشوفات الشهرية.", "Fees and monthly statements.")),
                ("compliance", T("التحقق والامتثال", "Verification"), T("التحقق من الهوية الوطنية والإقامة.", "National ID and iqama verification.")),
                ("support", T("دعم العملاء", "Customer support"), T("التحويلات والبطاقات.", "Transfers and cards.")),
            ],
            [
                new("support", TicketType.Incident, ["transfer"],
                    [T("التحويل معلّق", "Transfer is pending"), T("حوالة ما وصلت للمستفيد", "Transfer hasn't reached the beneficiary")],
                    T("السلام عليكم،\n\nحوّلت {amount} ريال قبل ساعتين والحالة لا زالت معلّقة. رقم العملية {ref}.", "Hello,\n\nI sent {amount} SAR two hours ago and it's still pending. Reference {ref}."),
                    T("التحويل قيد المراجعة الآلية المعتادة للمبالغ الكبيرة، ويكتمل خلال ساعة.", "The transfer is in the usual automatic review for larger amounts and completes within the hour."),
                    T("وصل الآن.", "It's arrived now."),
                    T("ممتاز، شكرًا لثقتك.", "Great, thanks for your trust."),
                    T("فحص احتيال روتيني، لا يوجد ما يدعو للقلق. @{teammate} للعلم.", "Routine fraud check, nothing to worry about. @{teammate} FYI.")),
                new("compliance", TicketType.Task, ["kyc"],
                    [T("التحقق من الهوية ما اكتمل", "Identity verification is stuck"), T("تحديث الإقامة المنتهية", "Update my expired iqama")],
                    T("مرحبًا،\n\nجددت الإقامة لكن الحساب لا زال مقيّدًا.", "Hi,\n\nI renewed my iqama but my account is still restricted."),
                    T("حدّثنا بيانات الهوية من مصدرها الرسمي ورفعنا القيد عن الحساب.", "We've refreshed your ID details from the official source and lifted the restriction."),
                    T("الحساب يعمل الآن.", "The account works now."),
                    T("شكرًا، نغلق الطلب.", "Thanks, closing this.")),
                new("support", TicketType.Problem, ["card"],
                    [T("البطاقة موقوفة", "My card is blocked"), T("رفض عملية شراء", "Card payment declined")],
                    T("السلام عليكم،\n\nبطاقتي انرفضت في محل في {city} مع إن الرصيد كافي.", "Hello,\n\nMy card was declined at a shop in {city} even though I have enough balance."),
                    T("أوقف النظام البطاقة احترازيًا بعد محاولة شراء دولية. أعدنا تفعيلها بعد تأكيدك.", "The card was paused as a precaution after an overseas attempt. We've reactivated it after your confirmation."),
                    T("تمام، اشتغلت.", "It works now."),
                    T("لأمانك ننصح بتفعيل إشعارات العمليات.", "We recommend turning on transaction alerts for your security.")),
                new("payments", TicketType.Question, ["fees", "invoice"],
                    [T("رسوم غير مفهومة في الكشف", "Unclear fee on my statement"), T("فاتورة رسوم الحساب التجاري", "Business account fee invoice")],
                    T("مرحبًا،\n\nظهرت رسوم {amount} ريال في كشف الحساب ولا أعرف سببها.", "Hi,\n\nThere's a {amount} SAR fee on my statement and I don't know what it's for."),
                    T("الرسوم مقابل تحويل دولي بتاريخ 12 من الشهر. أرفقنا التفاصيل.", "That's the fee for an international transfer on the 12th; details attached."),
                    T("واضح، شكرًا.", "Clear, thanks."),
                    T("العفو.", "You're welcome.")),
            ],
            [(T("التحويلات", "Transfers"), T("المدة والحدود.", "Times and limits.")),
             (T("الحساب والأمان", "Account and security"), T("التحقق وحماية البطاقة.", "Verification and card safety."))],
            [
                (0, T("كم تستغرق التحويلات؟", "How long do transfers take?"), T("التحويلات المحلية فورية في أغلب الأحيان، والدولية من يوم إلى ثلاثة أيام عمل.", "Local transfers are usually instant; international ones take 1 to 3 working days.")),
                (1, T("لماذا قُيّد حسابي؟", "Why was my account restricted?"), T("نقيّد الحساب عند انتهاء الهوية أو الإقامة. حدّث بياناتك ويُرفع القيد تلقائيًا.", "Accounts are restricted when an ID or iqama expires. Update it and the restriction lifts automatically.")),
            ],
            ("transaction_id", T("رقم العملية", "Transaction ID")),
            r => $"TX{Digits(r, 12)}"),

        new(
            "travel",
            T("للسفر والضيافة", "Travel"),
            T("أهلًا بك في {company}. نخدمك في حجوزات الفنادق والنقل لمكة المكرمة والمدينة المنورة وكل مدن المملكة.",
              "Welcome to {company}. We help with hotels and transport in Makkah, Madinah and across the Kingdom."),
            [
                ("payments", T("المدفوعات", "Payments"), T("الاسترجاع والفواتير.", "Refunds and invoices.")),
                ("bookings", T("الحجوزات", "Bookings"), T("الفنادق والنقل.", "Hotels and transport.")),
                ("guests", T("خدمة الضيوف", "Guest relations"), T("الطلبات الخاصة والشكاوى.", "Special requests and complaints.")),
            ],
            [
                new("bookings", TicketType.Task, ["hotel"],
                    [T("تعديل تاريخ حجز فندق في مكة", "Change my Makkah hotel dates"), T("إضافة ليلة للحجز {ref}", "Add a night to booking {ref}")],
                    T("السلام عليكم،\n\nعندي حجز {ref} في مكة وأبغى أقدّمه يومين بسبب تغيير موعد الرحلة.", "Hello,\n\nI have booking {ref} in Makkah and need to move it two days earlier because my flight changed."),
                    T("تم تعديل الحجز للتواريخ الجديدة بنفس الغرفة المطلة على الحرم.", "Done: your booking is moved to the new dates with the same Haram-view room."),
                    T("جزاكم الله خيرًا.", "Thank you so much."),
                    T("تقبّل الله منكم، رحلة موفقة.", "Have a blessed trip.")),
                new("bookings", TicketType.Question, ["transport"],
                    [T("النقل من مطار جدة", "Transfer from Jeddah airport"), T("سيارة من مكة للمدينة", "Car from Makkah to Madinah")],
                    T("مرحبًا،\n\nنحتاج سيارة لخمسة أشخاص من مطار الملك عبدالعزيز إلى الفندق في مكة.", "Hi,\n\nWe need a car for five people from King Abdulaziz Airport to our hotel in Makkah."),
                    T("حجزنا لكم سيارة عائلية، والسائق ينتظركم في صالة الوصول بلوحة باسمكم. جواله {mobile}.", "We've booked a family car; the driver will meet you at arrivals with a name sign. His mobile is {mobile}."),
                    T("وصلنا، شكرًا.", "We've arrived, thanks."),
                    T("حمدًا لله على السلامة.", "Welcome, glad you arrived safely.")),
                new("payments", TicketType.Question, ["refund"],
                    [T("استرجاع مبلغ حجز ملغي", "Refund for a cancelled booking"), T("رسوم الإلغاء", "Cancellation fee question")],
                    T("السلام عليكم،\n\nألغيت الحجز قبل أسبوعين من الموعد، كم المبلغ المسترد من {amount} ريال؟", "Hello,\n\nI cancelled two weeks before the stay. How much of the {amount} SAR is refunded?"),
                    T("الإلغاء قبل 14 يومًا مسترد بالكامل، وتم تنفيذ الاسترجاع اليوم.", "Cancellations 14 days ahead are fully refunded; we've processed it today."),
                    T("وصل المبلغ.", "The refund arrived."),
                    T("شكرًا لك.", "Thank you.")),
                new("payments", TicketType.Task, ["invoice", "corporate"],
                    [T("فاتورة حجز لشركة", "Booking invoice for a company"), T("فاتورة ضريبية للمجموعة", "VAT invoice for a group booking")],
                    T("مرحبًا،\n\nنحتاج فاتورة ضريبية لحجز مجموعة الموظفين باسم الشركة.", "Hi,\n\nWe need a VAT invoice for our staff group booking in the company name."),
                    T("أرسلنا الفاتورة الضريبية بالبيانات المطلوبة.", "We've sent the VAT invoice with your details."),
                    T("وصلت.", "Received."),
                    T("بالتوفيق.", "All the best.")),
            ],
            [(T("الحجوزات", "Bookings"), T("التعديل والإلغاء.", "Changes and cancellations.")),
             (T("النقل", "Transport"), T("المطار والتنقل بين المدن.", "Airport transfers and intercity travel."))],
            [
                (0, T("سياسة الإلغاء", "Cancellation policy"), T("- قبل 14 يومًا: استرداد كامل.\n- من 3 إلى 13 يومًا: استرداد 50٪.\n- أقل من 3 أيام: غير مسترد.", "- 14+ days ahead: full refund.\n- 3 to 13 days: 50% refund.\n- Under 3 days: non-refundable.")),
                (1, T("النقل من المطار", "Airport transfers"), T("يستقبلك السائق في صالة الوصول بلوحة باسمك، وتصلك بياناته قبل الرحلة بيوم.", "Your driver meets you at arrivals with a name sign; you get their details a day before.")),
            ],
            ("booking_ref", T("رقم الحجز", "Booking reference")),
            r => $"BK-{r.Next(10000, 99999)}"),

        new(
            "properties",
            T("العقارية", "Properties"),
            T("مرحبًا بك في {company}. اطلب صيانة أو استفسر عن عقدك.", "Welcome to {company}. Request maintenance or ask about your lease."),
            [
                ("payments", T("الإيجارات والسندات", "Rent and receipts"), T("دفعات الإيجار والسندات.", "Rent payments and receipts.")),
                ("maintenance", T("الصيانة", "Maintenance"), T("المكيفات والسباكة والكهرباء.", "AC, plumbing and electrical.")),
                ("leasing", T("التأجير", "Leasing"), T("العقود والتجديد.", "Leases and renewals.")),
            ],
            [
                new("maintenance", TicketType.Incident, ["ac"],
                    [T("المكيف ما يبرد", "The AC isn't cooling"), T("مكيف الصالة يسرّب ماء", "Living room AC is leaking")],
                    T("السلام عليكم،\n\nالمكيف في الشقة {ref} ما يبرد والجو حار جدًا. جوالي {mobile}.", "Hello,\n\nThe AC in unit {ref} isn't cooling and it's very hot. My mobile is {mobile}."),
                    T("أرسلنا فني التكييف اليوم بين 4 و6 مساءً.", "An AC technician is coming today between 4 and 6 pm."),
                    T("الفني وصل وقال يحتاج تعبئة فريون.", "The technician came and said it needs refrigerant."),
                    T("تمت تعبئة الفريون وتنظيف الفلاتر، والمكيف يعمل بشكل ممتاز.", "Refrigerant topped up and filters cleaned; the AC is working well."),
                    T("ثالث بلاغ لنفس الوحدة هذا الصيف. @{teammate} نقترح تغيير الكمبروسر.", "Third report for this unit this summer. @{teammate} suggest replacing the compressor.")),
                new("payments", TicketType.Question, ["receipt", "invoice"],
                    [T("طلب سند استلام الإيجار", "Rent receipt request"), T("فاتورة الدفعة الأخيرة", "Invoice for my last payment")],
                    T("مرحبًا،\n\nدفعت قسط الإيجار {amount} ريال وأحتاج سند الاستلام.", "Hi,\n\nI paid the rent instalment of {amount} SAR and need a receipt."),
                    T("أصدرنا السند وأرسلناه إلى بريدك وسُجلت الدفعة في العقد.", "We've issued the receipt by email and recorded the payment against your lease."),
                    T("وصل، شكرًا.", "Received, thanks."),
                    T("بالتوفيق.", "All the best.")),
                new("leasing", TicketType.Task, ["renewal"],
                    [T("تجديد عقد الإيجار", "Renew my lease"), T("إنهاء العقد مبكرًا", "End my lease early")],
                    T("السلام عليكم،\n\nعقدي ينتهي الشهر الجاي وأبغى أجدده لسنة.", "Hello,\n\nMy lease ends next month and I'd like to renew for a year."),
                    T("أرسلنا لك العقد الجديد لتوثيقه إلكترونيًا، بنفس قيمة الإيجار.", "We've sent the new lease for e-signing at the same rent."),
                    T("وقعت العقد.", "I've signed it."),
                    T("تم توثيق العقد، شكرًا لاستمرارك معنا.", "The lease is registered; thanks for staying with us.")),
                new("maintenance", TicketType.Problem, ["plumbing"],
                    [T("تسريب ماء في الحمام", "Water leak in the bathroom"), T("السخان خربان", "Water heater not working")],
                    T("مرحبًا،\n\nفيه تسريب ماء تحت مغسلة الحمام في الوحدة {ref}.", "Hi,\n\nThere's a leak under the bathroom sink in unit {ref}."),
                    T("السباك في الطريق إليك خلال ساعة.", "A plumber will be with you within the hour."),
                    T("تم، شكرًا على سرعة الاستجابة.", "Done, thanks for the quick response."),
                    T("تم تغيير الوصلة وإيقاف التسريب.", "The fitting was replaced and the leak stopped.")),
            ],
            [(T("الصيانة", "Maintenance"), T("أوقات الاستجابة.", "Response times.")),
             (T("العقود والإيجار", "Leases and rent"), T("الدفع والتجديد.", "Payment and renewal."))],
            [
                (0, T("أوقات الاستجابة للصيانة", "Maintenance response times"), T("**طارئ** (تسريب، كهرباء): خلال ساعتين.\n**عادي** (مكيف، سخان): خلال 24 ساعة.\n**تحسينات:** خلال أسبوع.", "**Emergency** (leaks, power): within 2 hours.\n**Standard** (AC, heaters): within 24 hours.\n**Improvements:** within a week.")),
                (1, T("طرق سداد الإيجار", "How to pay rent"), T("سدد عبر الحوالة البنكية أو مدى من التطبيق، ويصلك السند تلقائيًا.", "Pay by bank transfer or mada in the app; the receipt arrives automatically.")),
            ],
            ("unit_number", T("رقم الوحدة", "Unit number")),
            r => $"{(char)('A' + r.Next(6))}-{r.Next(101, 1240)}"),

        new(
            "cars",
            T("لتأجير السيارات", "Car Rental"),
            T("مرحبًا بك في {company}. نخدمك في الحجز والتمديد والمساعدة على الطريق.", "Welcome to {company}: bookings, extensions and roadside help."),
            [
                ("payments", T("المدفوعات والتأمين", "Payments and deposits"), T("مبالغ التأمين والمخالفات.", "Deposits and traffic fines.")),
                ("fleet", T("الأسطول والمساعدة", "Fleet and roadside"), T("الأعطال والمساعدة على الطريق.", "Breakdowns and roadside assistance.")),
                ("branches", T("الفروع", "Branches"), T("الحجوزات والاستلام والتسليم.", "Bookings, pick-up and drop-off.")),
            ],
            [
                new("payments", TicketType.Question, ["deposit"],
                    [T("مبلغ التأمين ما رجع", "Deposit not returned"), T("استرجاع التأمين بعد تسليم السيارة", "Deposit refund after returning the car")],
                    T("السلام عليكم،\n\nسلّمت السيارة قبل عشرة أيام ومبلغ التأمين {amount} ريال ما رجع.", "Hello,\n\nI returned the car ten days ago and the {amount} SAR deposit hasn't come back."),
                    T("ننتظر 7 أيام للتأكد من عدم وجود مخالفات مرورية، وتم الإفراج عن المبلغ اليوم.", "We wait 7 days to check for traffic fines; the deposit was released today."),
                    T("وصل المبلغ.", "Received."),
                    T("شكرًا لاختيارك لنا.", "Thanks for choosing us.")),
                new("payments", TicketType.Problem, ["fine", "invoice"],
                    [T("مخالفة مرورية على حسابي", "Traffic fine charged to me"), T("فاتورة مخالفة ساهر", "Invoice for a speed camera fine")],
                    T("مرحبًا،\n\nخصمتوا {amount} ريال مخالفة، وأنا ما كنت مستأجر السيارة بهذا التاريخ.", "Hi,\n\nYou charged me {amount} SAR for a fine on a date I didn't have the car."),
                    T("راجعنا السجل، والمخالفة لعميل آخر. أعدنا المبلغ لبطاقتك مع الاعتذار.", "We checked: the fine belongs to another renter. We've refunded your card with our apologies."),
                    T("شكرًا على المراجعة.", "Thanks for checking."),
                    T("نعتذر مرة أخرى عن الإزعاج.", "Sorry again for the trouble."),
                    T("خطأ في ربط رقم العقد {ref}. @{teammate} راجع بقية مخالفات الشهر.", "Wrong contract link on {ref}. @{teammate} please audit this month's fines.")),
                new("fleet", TicketType.Incident, ["breakdown"],
                    [T("السيارة تعطلت على الطريق", "Car broke down on the road"), T("كفر مبنشر على طريق الرياض الدمام", "Flat tyre on the Riyadh–Dammam road")],
                    T("السلام عليكم،\n\nالسيارة تعطلت على الطريق قرب {city}، جوالي {mobile}.", "Hello,\n\nThe car broke down near {city}. My mobile is {mobile}."),
                    T("سلامتك! أرسلنا سطحة وسيارة بديلة، الوصول خلال 45 دقيقة.", "Stay safe! A tow truck and a replacement car are on the way, about 45 minutes."),
                    T("وصلت السيارة البديلة.", "The replacement car has arrived."),
                    T("الحمد لله على السلامة، تم تسجيل البلاغ وإغلاقه.", "Glad you're safe; the case is closed.")),
                new("branches", TicketType.Task, ["extension"],
                    [T("تمديد مدة الإيجار", "Extend my rental"), T("تغيير فرع التسليم", "Return the car at another branch")],
                    T("مرحبًا،\n\nأبغى أمدد العقد {ref} ثلاثة أيام وأسلم السيارة في فرع {city}.", "Hi,\n\nI'd like to extend contract {ref} by three days and return the car in {city}."),
                    T("تم التمديد وإضافة التسليم في فرع {city} بدون رسوم إضافية.", "Extended, with drop-off in {city} at no extra charge."),
                    T("ممتاز.", "Excellent."),
                    T("رحلة سعيدة!", "Have a good trip!")),
            ],
            [(T("الحجز والتمديد", "Booking and extensions"), T("الفروع والمواعيد.", "Branches and times.")),
             (T("التأمين والمخالفات", "Deposits and fines"), T("الاسترجاع والمراجعة.", "Refunds and disputes."))],
            [
                (0, T("كيف أمدد الإيجار؟", "How do I extend a rental?"), T("من التطبيق: **حجوزاتي** ثم **تمديد**، أو تواصل معنا قبل انتهاء العقد بـ 6 ساعات.", "In the app: **My bookings**, then **Extend**, or contact us at least 6 hours before the end.")),
                (1, T("متى يُسترد مبلغ التأمين؟", "When is the deposit refunded?"), T("بعد 7 أيام من التسليم للتأكد من عدم وجود مخالفات.", "Seven days after return, once we've checked for traffic fines.")),
            ],
            ("contract_number", T("رقم العقد", "Contract number")),
            r => $"RA-{Digits(r, 7)}"),
    ];
}
