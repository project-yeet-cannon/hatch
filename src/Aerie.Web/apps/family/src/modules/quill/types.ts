/*
  The wire shapes, mirroring src/Aerie.Api/Modules/Quill/Dtos.cs. Hand-written
  rather than generated, like every other Aerie SPA's types.ts.

  There is deliberately no summary shape: the list endpoint returns whole notes.
  That is what makes the offline mirror complete rather than a cache of whatever
  happened to be opened - see store.ts, and the tripwire in Dtos.cs.

  PersonId is absent on purpose. The server decides whose notes these are from
  the grant behind the request; a client that could name a person is a client
  that could name a different one.
*/

export interface Note {
  id: string;
  /** Blank for an untitled note - the ordinary case. The placeholder is the client's business. */
  title: string;
  body: string;
  createdAt: string;
  updatedAt: string;
}

/** A create or an edit. Both fields overwrite; there is no second writer to merge with. */
export interface NoteWriteRequest {
  title: string;
  body: string;
}
