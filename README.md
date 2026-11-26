# Company Brain

One memory for Salesforce, HubSpot and Airtable, written in F#. It does three things:

1. **Answers questions in plain language** across all three CRMs, e.g. *"Which deals over $100k have gone quiet for a month, and what do we know about why?"*
2. **Makes changes for you**: logs calls, creates follow-up tasks, moves deal stages. **Every change is shown to you and needs a `y` before it reaches a CRM**, and every proposal is written to an audit log.
3. **Holds knowledge the CRM doesn't**: meeting notes, account plans, pricing rules, things people know. These are linked to the right account and searchable next to CRM data.

## How it works

```
 Salesforce ─┐                      ┌─ sql_query (read-only)
 HubSpot ────┼─ sync ─▶ brain.db ◀──┼─ search_knowledge (full-text)      ◀─ Claude ◀─ you
 Airtable ───┘          (SQLite)    ├─ account_overview / stale_deals
 notes/*.md ── ingest ──▶           ├─ remember (local note)
                                    └─ log_activity / create_task / update_deal_stage
                                         └─▶ your approval ─▶ the CRM that owns the record
```

- **Sync** copies accounts, contacts, deals and activities into one local SQLite file, in one shared shape. Ids look like `hubspot:123`, `salesforce:006…` or `airtable:rec…`, so the brain always knows which CRM owns a record.
- **Ask** gives Claude the tools above. It writes its own SQL for anything countable. That SQL runs on a read-only database connection, so it can't change anything. It uses full-text search for the "why".
- **Writes** go back to the CRM the record came from. They are also mirrored locally, so follow-up questions see the change straight away.

## Quick start (no CRM needed)

```bash
dotnet build src/CompanyBrain
```

```bash
alias brain="dotnet run --project src/CompanyBrain --"
```

```bash
brain demo
```

```bash
brain stale 30 100000
```

```bash
export ANTHROPIC_API_KEY=sk-ant-...
```

```bash
brain ask "Which big deals have gone quiet, and what do our notes say about them?"
```

```bash
brain chat
```

## Connect your CRMs

Copy `.env.example` to `.env` (in the folder you run `brain` from) and fill in the CRMs you use:

| CRM | What you need |
|---|---|
| Salesforce | `SF_INSTANCE_URL`, plus either `SF_ACCESS_TOKEN` or a Connected App with the client-credentials flow (`SF_CLIENT_ID`, `SF_CLIENT_SECRET`) |
| HubSpot | A private app token (`HUBSPOT_TOKEN`) with read/write on companies, contacts, deals, notes, calls, meetings and tasks, plus owners read |
| Airtable | A personal access token and base id. Tables default to `Accounts`, `Contacts`, `Deals`, `Activities` (override in `.env`). Common field names are matched automatically (`Name`/`Company`, `Stage`/`Status`, `Amount`/`Value`, linked `Account`/`Deal` fields…) |

Then:

```bash
brain sync
```

```bash
brain status
```

Run `brain sync` whenever you want fresh data. A cron job or scheduled task every 15–60 minutes works well.

## Feed it knowledge

```bash
brain ingest ~/Documents/account-notes
```

```bash
brain remember "Northwind's IT director has veto power and prefers on-prem" --account Northwind
```

Notes are linked to the account they mention. Re-importing a folder updates notes instead of duplicating them. In a chat you can also just say *"remember that…"*.

## All commands

| Command | What it does |
|---|---|
| `brain demo` | Load made-up demo data |
| `brain sync [salesforce\|hubspot\|airtable]` | Pull from connected CRMs |
| `brain ask "…"` / `brain chat` | Talk to the brain (needs `ANTHROPIC_API_KEY`) |
| `brain remember "…" [--account NAME]` | Save a note |
| `brain ingest <folder>` | Import `.md`/`.txt` notes |
| `brain stale [days] [min_amount]` | Open deals with no activity |
| `brain account <name>` | Everything about one account |
| `brain search <words>` | Search notes and call write-ups |
| `brain sql "SELECT …"` | Read-only SQL |
| `brain status` / `brain audit` | What's stored / every proposed change |
| `brain tool <name> '<json>'` | Run one of Claude's tools by hand (testing) |

## Safety

- Claude's SQL runs on a connection SQLite opens read-only. Only a single `SELECT`/`WITH` is accepted.
- Every CRM write needs an explicit `y`. Declined and failed writes are logged in `audit_log` too.
- Credentials stay in your environment. Claude sees query results, never tokens.
- The model is `claude-opus-5-5`, with medium effort. If a safety check declines a request, the API's server-side fallback retries it on another model in the same call.

## Code map (`src/CompanyBrain`)

| File | Role |
|---|---|
| `Domain.fs` | Shared record types and the `WriteAction` union |
| `Store.fs` | SQLite schema, sync upserts, full-text search, built-in queries |
| `Salesforce.fs`, `HubSpot.fs`, `Airtable.fs`, `Demo.fs` | Connectors: `Pull` everything in, `Apply` a write |
| `Actions.fs` | Routes a write to the right CRM, asks you, records it |
| `Tools.fs` | The tools Claude can call |
| `Agent.fs` | The Claude conversation loop |
| `Knowledge.fs` | Note import and quick notes |
| `Program.fs` | Command line |

To add another CRM, write a module that returns a `Connector` (`Pull` plus `Apply`) and register it in `Program.connectors`.
