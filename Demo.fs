/// Made-up companies and deals, so you can try the brain before connecting a real CRM.
/// Writes in demo mode only touch the local database.
module CompanyBrain.Demo

open System
open System.Threading.Tasks
open CompanyBrain.Domain
open CompanyBrain.Connector

let private src = Source.Demo
let private did = makeId src
let private daysAgo (n: int) = DateTime.UtcNow.Date.AddDays(float -n).AddHours 14.0
let private inDays (n: int) = DateTime.UtcNow.Date.AddDays(float n)

let private account ext name domain industry owner =
    { Id = did ext
      Source = src
      ExternalId = ext
      Name = name
      Domain = Some domain
      Industry = Some industry
      Owner = Some owner
      UpdatedAt = daysAgo 3 }

let private contact ext acct name email title =
    { Id = did ext
      Source = src
      ExternalId = ext
      AccountId = Some(did acct)
      Name = name
      Email = Some email
      Title = Some title
      UpdatedAt = daysAgo 10 }

let private deal ext acct name stage (amount: int) closeIn owner closed updated =
    { Id = did ext
      Source = src
      ExternalId = ext
      AccountId = Some(did acct)
      Name = name
      Stage = stage
      Amount = Some(decimal amount)
      CloseDate = Some(inDays closeIn)
      Owner = Some owner
      IsClosed = closed
      UpdatedAt = daysAgo updated }

let private act ext kind subject body acct dealExt ago =
    { Id = did ext
      Source = src
      ExternalId = ext
      Kind = kind
      Subject = subject
      Body = body
      AccountId = Some(did acct)
      DealId = dealExt |> Option.map did
      ContactId = None
      OccurredAt = daysAgo ago
      DueAt = None
      Done = true }

let snapshot () =
    { Accounts =
        [ account "acc-1" "Northwind Logistics" "northwind.example" "Logistics" "Maya Chen"
          account "acc-2" "Helios Energy" "helios.example" "Energy" "Daniel Okafor"
          account "acc-3" "Bluefin Health" "bluefin.example" "Healthcare" "Maya Chen"
          account "acc-4" "Quarry Labs" "quarrylabs.example" "Software" "Priya Raman"
          account "acc-5" "Orchard Retail Group" "orchard.example" "Retail" "Daniel Okafor"
          account "acc-6" "Tidewater Bank" "tidewater.example" "Financial Services" "Priya Raman" ]
      Contacts =
        [ contact "con-1" "acc-1" "Sara Lindqvist" "sara@northwind.example" "VP Operations"
          contact "con-2" "acc-1" "Tom Becker" "tom@northwind.example" "IT Director"
          contact "con-3" "acc-2" "Amina Yusuf" "amina@helios.example" "CFO"
          contact "con-4" "acc-3" "Dr. Lee Park" "lee@bluefin.example" "Chief Medical Officer"
          contact "con-5" "acc-4" "Jonas Weber" "jonas@quarrylabs.example" "CTO"
          contact "con-6" "acc-5" "Rosa Martín" "rosa@orchard.example" "Head of Procurement"
          contact "con-7" "acc-6" "Ken Ito" "ken@tidewater.example" "CISO" ]
      Deals =
        [ deal "deal-1" "acc-1" "Northwind — Fleet analytics platform" "Negotiation" 240000 21 "Maya Chen" false 40
          deal "deal-2" "acc-2" "Helios — Grid monitoring expansion" "Proposal" 410000 45 "Daniel Okafor" false 52
          deal "deal-3" "acc-3" "Bluefin — Patient intake pilot" "Discovery" 60000 60 "Maya Chen" false 5
          deal "deal-4" "acc-4" "Quarry Labs — Team plan upgrade" "Closed Won" 36000 -12 "Priya Raman" true 12
          deal "deal-5" "acc-5" "Orchard — Store ops rollout (120 sites)" "Proposal" 520000 30 "Daniel Okafor" false 34
          deal "deal-6" "acc-6" "Tidewater — Security review add-on" "Negotiation" 95000 14 "Priya Raman" false 8
          deal "deal-7" "acc-1" "Northwind — Driver app add-on" "Qualification" 30000 90 "Maya Chen" false 70
          deal "deal-8" "acc-3" "Bluefin — Data residency module" "Closed Lost" 80000 -30 "Maya Chen" true 30 ]
      Activities =
        [ act "act-1" "call" "Pricing call with Sara" (Some "Sara pushed back on the per-vehicle price. Wants a 3-year term with a 12% discount. Legal review of the DPA still outstanding on their side.") "acc-1" (Some "deal-1") 41
          act "act-2" "email" "Sent revised proposal" (Some "Sent v2 of the proposal with a phased rollout: 2 substations first, then the full grid in Q3.") "acc-2" (Some "deal-2") 53
          act "act-3" "meeting" "Discovery workshop" (Some "Dr. Park's team spends ~20 minutes per intake on paper forms. Strong interest; budget sits with the ops committee that meets monthly.") "acc-3" (Some "deal-3") 5
          act "act-4" "meeting" "Kickoff" (Some "Upgrade signed. Jonas wants SSO set up before the team grows past 50 seats.") "acc-4" (Some "deal-4") 12
          act "act-5" "call" "Procurement check-in" (Some "Rosa says the board approves capex in their next quarterly meeting. A competitor is in the running and is cheaper per site.") "acc-5" (Some "deal-5") 35
          act "act-6" "meeting" "Security questionnaire review" (Some "Ken walked through the questionnaire. Two open items: pen-test report and SOC 2 bridge letter.") "acc-6" (Some "deal-6") 8
          act "act-7" "email" "Intro to driver app" None "acc-1" (Some "deal-7") 72
          { act "act-8" "task" "Send SOC 2 bridge letter to Ken" None "acc-6" (Some "deal-6") 8 with
              DueAt = Some(inDays 2)
              Done = false } ] }

/// Knowledge that lives outside the CRM — the kind of thing people otherwise keep in their heads.
let notes () =
    [ { Id = "note:demo-1"
        Title = "Northwind account plan"
        Body =
          "Sara Lindqvist is the champion but Tom Becker (IT) has veto power and prefers on-prem. Their fiscal year ends in March, so they push to sign before then. They churned from a competitor in 2024 over poor support — lead with our support SLAs."
        AccountId = Some(did "acc-1")
        DealId = Some(did "deal-1")
        Author = Some "Maya Chen"
        SourcePath = None
        CreatedAt = daysAgo 60 }
      { Id = "note:demo-2"
        Title = "Pricing guardrails"
        Body =
          "Discounts above 15% need VP Sales approval. Multi-year deals can go to 12% without approval. Never discount the support tier."
        AccountId = None
        DealId = None
        Author = Some "Sales Ops"
        SourcePath = None
        CreatedAt = daysAgo 120 }
      { Id = "note:demo-3"
        Title = "Orchard: competitor intel"
        Body =
          "The competing vendor at Orchard is roughly 20% cheaper per site but has no offline mode. Orchard's rural stores lose connectivity often — offline mode is our wedge."
        AccountId = Some(did "acc-5")
        DealId = Some(did "deal-5")
        Author = Some "Daniel Okafor"
        SourcePath = None
        CreatedAt = daysAgo 33 } ]

let connector =
    { Source = src
      Pull = fun () -> Task.FromResult(snapshot ())
      Apply = fun _ _ -> Task.FromResult("demo-" + Guid.NewGuid().ToString("N").Substring(0, 8)) }
