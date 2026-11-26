/// What every CRM connector provides: pull everything in, and apply an approved change.
module CompanyBrain.Connector

open System.Threading.Tasks
open CompanyBrain.Domain

type TargetKind =
    | DealTarget
    | AccountTarget

/// The record a write is aimed at, already resolved to the owning CRM's own id.
type Target =
    { LocalId: string
      Kind: TargetKind
      ExternalId: string
      /// For a deal: its account's id in the same CRM, when known.
      AccountExternalId: string option }

type Connector =
    { Source: Source
      Pull: unit -> Task<Snapshot>
      /// Makes the change in the CRM and returns the CRM's id for anything it created.
      Apply: Target -> WriteAction -> Task<string> }
