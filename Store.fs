/// The brain's memory: a local SQLite copy of every CRM plus notes, with full-text search.
module CompanyBrain.Store

open System
open System.Globalization
open System.Text.Json
open Microsoft.Data.Sqlite
open CompanyBrain.Domain

let schema =
    """
CREATE TABLE IF NOT EXISTS accounts (
  id TEXT PRIMARY KEY, source TEXT NOT NULL, external_id TEXT NOT NULL,
  name TEXT NOT NULL, domain TEXT, industry TEXT, owner TEXT, updated_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS contacts (
  id TEXT PRIMARY KEY, source TEXT NOT NULL, external_id TEXT NOT NULL, account_id TEXT,
  name TEXT NOT NULL, email TEXT, title TEXT, updated_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS deals (
  id TEXT PRIMARY KEY, source TEXT NOT NULL, external_id TEXT NOT NULL, account_id TEXT,
  name TEXT NOT NULL, stage TEXT NOT NULL, amount REAL, close_date TEXT, owner TEXT,
  is_closed INTEGER NOT NULL, updated_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS activities (
  id TEXT PRIMARY KEY, source TEXT NOT NULL, external_id TEXT NOT NULL, kind TEXT NOT NULL,
  subject TEXT NOT NULL, body TEXT, account_id TEXT, deal_id TEXT, contact_id TEXT,
  occurred_at TEXT NOT NULL, due_at TEXT, done INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS notes (
  id TEXT PRIMARY KEY, title TEXT NOT NULL, body TEXT NOT NULL, account_id TEXT, deal_id TEXT,
  author TEXT, source_path TEXT, created_at TEXT NOT NULL);
CREATE VIRTUAL TABLE IF NOT EXISTS knowledge_fts USING fts5(
  ref_id UNINDEXED, kind UNINDEXED, title, body, tokenize = 'porter unicode61');
CREATE TABLE IF NOT EXISTS audit_log (
  id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, source TEXT NOT NULL,
  action TEXT NOT NULL, approved INTEGER NOT NULL, result TEXT);
CREATE TABLE IF NOT EXISTS sync_state (source TEXT PRIMARY KEY, last_sync TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_deals_account ON deals(account_id);
CREATE INDEX IF NOT EXISTS ix_act_deal ON activities(deal_id);
CREATE INDEX IF NOT EXISTS ix_act_account ON activities(account_id);
CREATE INDEX IF NOT EXISTS ix_contacts_account ON contacts(account_id);
"""

/// Plain-English description of the tables, handed to Claude so it can write its own queries.
let schemaForModel =
    """Tables (SQLite). Dates are ISO-8601 text like 2026-03-01T14:00:00Z. Compare them with julianday(),
e.g. julianday(occurred_at) >= julianday('now', '-30 days'); never compare to datetime('now') as text.
accounts(id, source, external_id, name, domain, industry, owner, updated_at)
contacts(id, source, external_id, account_id -> accounts.id, name, email, title, updated_at)
deals(id, source, external_id, account_id -> accounts.id, name, stage, amount, close_date, owner, is_closed 0/1, updated_at)
activities(id, source, external_id, kind ['call','email','meeting','task','note'], subject, body,
           account_id, deal_id, contact_id, occurred_at, due_at, done 0/1)
notes(id, title, body, account_id, deal_id, author, source_path, created_at)  -- knowledge outside the CRM
audit_log(id, at, source, action, approved, result)  -- every write the brain proposed
source is one of 'salesforce', 'hubspot', 'airtable', 'demo'. ids look like '<source>:<external id>'."""

let private iso (d: DateTime) =
    d.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)

let private isoOpt =
    function
    | Some d -> box (iso d)
    | None -> box DBNull.Value

let private opt (v: 'a option) =
    match v with
    | Some x -> box x
    | None -> box DBNull.Value

let openDb (path: string) =
    let conn = new SqliteConnection($"Data Source={path}")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "PRAGMA journal_mode=WAL;" + schema
    cmd.ExecuteNonQuery() |> ignore
    conn

/// A second connection that SQLite itself refuses to write through. Claude's SQL runs here.
let openReadOnly (path: string) =
    let conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly")
    conn.Open()
    conn

let private exec (conn: SqliteConnection) (tx: SqliteTransaction) (sql: string) (ps: (string * obj) list) =
    use cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- sql

    for (k, v) in ps do
        cmd.Parameters.AddWithValue(k, v) |> ignore

    cmd.ExecuteNonQuery() |> ignore

let private upsertFts (conn: SqliteConnection) tx (refId: string) (kind: string) (title: string) (body: string) =
    exec conn tx "DELETE FROM knowledge_fts WHERE ref_id = $id" [ "$id", box refId ]

    exec
        conn
        tx
        "INSERT INTO knowledge_fts(ref_id, kind, title, body) VALUES ($id, $k, $t, $b)"
        [ "$id", box refId; "$k", box kind; "$t", box title; "$b", box body ]

let private saveRecords (conn: SqliteConnection) (source: Source) (snap: Snapshot) (markSynced: bool) =
    use tx = conn.BeginTransaction()

    for a in snap.Accounts do
        exec
            conn
            tx
            """INSERT OR REPLACE INTO accounts VALUES ($id,$s,$x,$n,$d,$i,$o,$u)"""
            [ "$id", box a.Id
              "$s", box a.Source.Key
              "$x", box a.ExternalId
              "$n", box a.Name
              "$d", opt a.Domain
              "$i", opt a.Industry
              "$o", opt a.Owner
              "$u", box (iso a.UpdatedAt) ]

    for c in snap.Contacts do
        exec
            conn
            tx
            """INSERT OR REPLACE INTO contacts VALUES ($id,$s,$x,$a,$n,$e,$t,$u)"""
            [ "$id", box c.Id
              "$s", box c.Source.Key
              "$x", box c.ExternalId
              "$a", opt c.AccountId
              "$n", box c.Name
              "$e", opt c.Email
              "$t", opt c.Title
              "$u", box (iso c.UpdatedAt) ]

    for d in snap.Deals do
        exec
            conn
            tx
            """INSERT OR REPLACE INTO deals VALUES ($id,$s,$x,$a,$n,$st,$am,$cd,$o,$c,$u)"""
            [ "$id", box d.Id
              "$s", box d.Source.Key
              "$x", box d.ExternalId
              "$a", opt d.AccountId
              "$n", box d.Name
              "$st", box d.Stage
              "$am", (d.Amount |> Option.map float |> opt)
              "$cd", isoOpt d.CloseDate
              "$o", opt d.Owner
              "$c", box (if d.IsClosed then 1 else 0)
              "$u", box (iso d.UpdatedAt) ]

    for a in snap.Activities do
        exec
            conn
            tx
            """INSERT OR REPLACE INTO activities VALUES ($id,$s,$x,$k,$sub,$b,$a,$d,$c,$at,$due,$done)"""
            [ "$id", box a.Id
              "$s", box a.Source.Key
              "$x", box a.ExternalId
              "$k", box a.Kind
              "$sub", box a.Subject
              "$b", opt a.Body
              "$a", opt a.AccountId
              "$d", opt a.DealId
              "$c", opt a.ContactId
              "$at", box (iso a.OccurredAt)
              "$due", isoOpt a.DueAt
              "$done", box (if a.Done then 1 else 0) ]

        // Call and meeting write-ups are knowledge too, so they're searchable alongside notes.
        match a.Body with
        | Some body when body.Length > 20 -> upsertFts conn tx a.Id "activity" a.Subject body
        | _ -> ()

    if markSynced then
        exec
            conn
            tx
            "INSERT OR REPLACE INTO sync_state VALUES ($s, $t)"
            [ "$s", box source.Key; "$t", box (iso DateTime.UtcNow) ]

    tx.Commit()

/// Writes a pulled snapshot. Records are upserted, so re-syncing is safe.
let saveSnapshot conn source snap = saveRecords conn source snap true

let saveNote (conn: SqliteConnection) (note: Note) =
    use tx = conn.BeginTransaction()

    exec
        conn
        tx
        "INSERT OR REPLACE INTO notes VALUES ($id,$t,$b,$a,$d,$au,$p,$c)"
        [ "$id", box note.Id
          "$t", box note.Title
          "$b", box note.Body
          "$a", opt note.AccountId
          "$d", opt note.DealId
          "$au", opt note.Author
          "$p", opt note.SourcePath
          "$c", box (iso note.CreatedAt) ]

    upsertFts conn tx note.Id "note" note.Title note.Body
    tx.Commit()

let audit (conn: SqliteConnection) (source: string) (action: string) (approved: bool) (result: string) =
    use tx = conn.BeginTransaction()

    exec
        conn
        tx
        "INSERT INTO audit_log(at, source, action, approved, result) VALUES ($at,$s,$a,$ap,$r)"
        [ "$at", box (iso DateTime.UtcNow)
          "$s", box source
          "$a", box action
          "$ap", box (if approved then 1 else 0)
          "$r", box result ]

    tx.Commit()

/// Mirrors an approved write locally, so the brain sees it before the next sync.
let recordLocalActivity (conn: SqliteConnection) (act: Activity) =
    saveRecords conn act.Source { Snapshot.Empty with Activities = [ act ] } false

let setDealStage (conn: SqliteConnection) (dealId: string) (stage: string) =
    use tx = conn.BeginTransaction()

    exec
        conn
        tx
        "UPDATE deals SET stage = $s, updated_at = $u WHERE id = $id"
        [ "$s", box stage; "$u", box (iso DateTime.UtcNow); "$id", box dealId ]

    tx.Commit()

// ---------- reading ----------

let private readerToRows (r: SqliteDataReader) (maxRows: int) =
    let cols = [ for i in 0 .. r.FieldCount - 1 -> r.GetName i ]
    let rows = ResizeArray<Map<string, obj>>()
    let mutable truncated = false

    while r.Read() && not truncated do
        if rows.Count >= maxRows then
            truncated <- true
        else
            rows.Add(
                cols
                |> List.mapi (fun i c -> c, (if r.IsDBNull i then null else r.GetValue i))
                |> Map.ofList
            )

    cols, List.ofSeq rows, truncated

/// Keeps é, — and friends readable instead of \u escapes (also fewer tokens for Claude).
let jsonOptions =
    JsonSerializerOptions(Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

/// Rows as compact JSON — the format Claude reads tool results in.
let rowsToJson (cols: string list, rows: Map<string, obj> list, truncated: bool) =
    let data =
        rows
        |> List.map (fun row ->
            let d = Collections.Generic.Dictionary<string, obj>()

            for c in cols do
                d[c] <- row[c]

            d)

    JsonSerializer.Serialize(
        {| rowCount = rows.Length
           truncated = truncated
           rows = data |},
        jsonOptions
    )

let query (conn: SqliteConnection) (sql: string) (ps: (string * obj) list) (maxRows: int) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql

    for (k, v) in ps do
        cmd.Parameters.AddWithValue(k, v) |> ignore

    use r = cmd.ExecuteReader()
    readerToRows r maxRows

/// Runs model-written SQL. Must be a single SELECT/WITH, and the connection is read-only anyway.
let runReadOnlySql (ro: SqliteConnection) (sql: string) =
    let trimmed = sql.Trim().TrimEnd(';').Trim()
    let lower = trimmed.ToLowerInvariant()

    if not (lower.StartsWith "select" || lower.StartsWith "with") then
        Error "Only SELECT (or WITH ... SELECT) queries are allowed."
    elif trimmed.Contains ";" then
        Error "Send one statement at a time."
    else
        try
            Ok(query ro trimmed [] 200 |> rowsToJson)
        with ex ->
            Error $"SQL error: {ex.Message}"

/// Full-text search over notes and activity write-ups.
let searchKnowledge (conn: SqliteConnection) (text: string) (limit: int) =
    // Quote each word so punctuation in the question can't break FTS syntax.
    let terms =
        text.Split([| ' '; ','; '.'; '?'; '!'; ':'; ';'; '"'; '\''; '('; ')' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.filter (fun w -> w.Length > 1)
        |> Array.map (fun w -> "\"" + w.Replace("\"", "") + "\"")

    if terms.Length = 0 then
        rowsToJson ([], [], false)
    else
        let ftsQuery = String.Join(" OR ", terms)

        query
            conn
            """SELECT k.ref_id, k.kind, k.title, snippet(knowledge_fts, 3, '[', ']', '…', 40) AS excerpt,
                      COALESCE(n.account_id, a.account_id) AS account_id,
                      COALESCE(n.deal_id, a.deal_id) AS deal_id,
                      COALESCE(n.created_at, a.occurred_at) AS at
               FROM knowledge_fts k
               LEFT JOIN notes n ON n.id = k.ref_id
               LEFT JOIN activities a ON a.id = k.ref_id
               WHERE knowledge_fts MATCH $q
               ORDER BY bm25(knowledge_fts) LIMIT $lim"""
            [ "$q", box ftsQuery; "$lim", box limit ]
            limit
        |> rowsToJson

/// Open deals with no activity in N days — the classic "what's slipping?" question.
let staleDeals (conn: SqliteConnection) (days: int) (minAmount: float) =
    query
        conn
        """SELECT d.id, d.name, a.name AS account, d.stage, d.amount, d.owner, d.close_date,
                  MAX(act.occurred_at) AS last_activity,
                  CAST(julianday('now') - julianday(COALESCE(MAX(act.occurred_at), d.updated_at)) AS INTEGER) AS days_quiet
           FROM deals d
           LEFT JOIN accounts a ON a.id = d.account_id
           LEFT JOIN activities act ON act.deal_id = d.id AND julianday(act.occurred_at) <= julianday('now')
           WHERE d.is_closed = 0 AND COALESCE(d.amount, 0) >= $min
           GROUP BY d.id
           HAVING days_quiet >= $days
           ORDER BY d.amount DESC"""
        [ "$days", box days; "$min", box minAmount ]
        100
    |> rowsToJson

/// Everything about one account: details, deals, people, recent activity, notes.
let accountOverview (conn: SqliteConnection) (nameOrId: string) =
    let found =
        query
            conn
            "SELECT * FROM accounts WHERE id = $q OR name LIKE $like ORDER BY (id = $q) DESC, length(name) LIMIT 1"
            [ "$q", box nameOrId; "$like", box ("%" + nameOrId + "%") ]
            1

    match found with
    | _, [], _ -> None
    | cols, row :: _, _ ->
        let id = string row["id"]
        let p = [ "$id", box id ]

        let section sql = query conn sql p 25 |> rowsToJson

        Some(
            sprintf
                """{"account":%s,"deals":%s,"contacts":%s,"recent_activity":%s,"open_tasks":%s,"notes":%s}"""
                (rowsToJson (cols, [ row ], false))
                (section "SELECT id, name, stage, amount, close_date, owner, is_closed FROM deals WHERE account_id = $id ORDER BY is_closed, amount DESC")
                (section "SELECT id, name, title, email FROM contacts WHERE account_id = $id")
                (section
                    """SELECT id, kind, subject, substr(body, 1, 300) AS body, occurred_at, deal_id FROM activities
                       WHERE (account_id = $id OR deal_id IN (SELECT id FROM deals WHERE account_id = $id))
                         AND kind <> 'task' ORDER BY occurred_at DESC LIMIT 15""")
                (section
                    """SELECT id, subject, due_at, deal_id FROM activities
                       WHERE (account_id = $id OR deal_id IN (SELECT id FROM deals WHERE account_id = $id))
                         AND kind = 'task' AND done = 0 ORDER BY due_at""")
                (section "SELECT id, title, substr(body, 1, 600) AS body, author, created_at FROM notes WHERE account_id = $id ORDER BY created_at DESC")
        )

/// Finds a deal or account by id, so a write can be routed to the CRM that owns it.
let lookupTarget (conn: SqliteConnection) (id: string) =
    let deal = query conn "SELECT id, source, external_id, account_id, name FROM deals WHERE id = $id" [ "$id", box id ] 1

    match deal with
    | _, row :: _, _ -> Some("deal", row)
    | _ ->
        match query conn "SELECT id, source, external_id, name FROM accounts WHERE id = $id" [ "$id", box id ] 1 with
        | _, row :: _, _ -> Some("account", row)
        | _ -> None

/// Links a free-text note to an account whose name or domain it mentions.
let guessAccount (conn: SqliteConnection) (text: string) =
    let _, rows, _ = query conn "SELECT id, name, domain FROM accounts" [] 10000
    let lower = text.ToLowerInvariant()

    let mentions (word: string) =
        Text.RegularExpressions.Regex.IsMatch(lower, @"\b" + Text.RegularExpressions.Regex.Escape word + @"\b")

    rows
    |> List.filter (fun r ->
        let name = (string r["name"]).ToLowerInvariant()
        let domain = match r["domain"] with null -> "" | d -> (string d).ToLowerInvariant()
        // "Helios" should find "Helios Energy": also accept a distinctive first word of the name.
        let firstWord = name.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> Array.tryHead |> Option.defaultValue ""
        (name.Length > 2 && mentions name)
        || (firstWord.Length >= 4 && mentions firstWord)
        || (domain <> "" && lower.Contains domain))
    |> List.sortByDescending (fun r -> (string r["name"]).Length)
    |> List.tryHead
    |> Option.map (fun r -> string r["id"])

let stats (conn: SqliteConnection) =
    let count table =
        let _, rows, _ = query conn $"SELECT source, COUNT(*) AS n FROM {table} GROUP BY source" [] 10
        rows |> List.map (fun r -> $"""{r["source"]}={r["n"]}""") |> String.concat ", "

    let _, notes, _ = query conn "SELECT COUNT(*) AS n FROM notes" [] 1
    let _, syncs, _ = query conn "SELECT source, last_sync FROM sync_state" [] 10

    [ "accounts", count "accounts"
      "contacts", count "contacts"
      "deals", count "deals"
      "activities", count "activities"
      "notes", string notes.Head["n"]
      "last sync", (syncs |> List.map (fun r -> $"""{r["source"]} @ {r["last_sync"]}""") |> String.concat ", ") ]
