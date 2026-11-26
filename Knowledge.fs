/// Brings knowledge that lives outside the CRM into the brain: folders of notes, quick one-liners.
module CompanyBrain.Knowledge

open System
open System.IO
open Microsoft.Data.Sqlite
open CompanyBrain.Domain

let private extensions = set [ ".md"; ".txt"; ".markdown" ]

/// Title is the first markdown heading, else the file name.
let private titleOf (path: string) (body: string) =
    body.Split('\n')
    |> Array.tryFind (fun l -> l.TrimStart().StartsWith "#")
    |> Option.map (fun l -> l.TrimStart('#', ' ').Trim())
    |> Option.defaultValue (Path.GetFileNameWithoutExtension path)

/// Imports every .md/.txt file under a folder. Each note is linked to the account it mentions, if any.
/// Re-importing the same file updates it rather than duplicating it.
let ingestFolder (conn: SqliteConnection) (folder: string) =
    let files =
        Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
        |> Seq.filter (fun f -> extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
        |> List.ofSeq

    [ for file in files do
          let body = File.ReadAllText file
          let full = Path.GetFullPath file
          let account = Store.guessAccount conn (Path.GetFileName file + "\n" + body)

          let note =
              { Id = "note:file:" + Convert.ToHexString(Security.Cryptography.SHA1.HashData(Text.Encoding.UTF8.GetBytes full)).Substring(0, 16)
                Title = titleOf file body
                Body = body
                AccountId = account
                DealId = None
                Author = None
                SourcePath = Some full
                CreatedAt = File.GetLastWriteTimeUtc file }

          Store.saveNote conn note
          note ]

/// A quick note typed on the command line.
let remember (conn: SqliteConnection) (text: string) (accountHint: string option) =
    let account =
        match accountHint with
        | Some hint ->
            Store.accountOverview conn hint
            |> Option.bind (fun json ->
                let doc = Text.Json.JsonDocument.Parse json
                let rows = doc.RootElement.GetProperty("account").GetProperty("rows")
                if rows.GetArrayLength() > 0 then Some(rows[0].GetProperty("id").GetString()) else None)
        | None -> Store.guessAccount conn text

    let title = if text.Length > 60 then text.Substring(0, 60) + "…" else text

    let note =
        { Id = $"note:{Guid.NewGuid():N}"
          Title = title
          Body = text
          AccountId = account
          DealId = None
          Author = Some Environment.UserName
          SourcePath = None
          CreatedAt = DateTime.UtcNow }

    Store.saveNote conn note
    note
