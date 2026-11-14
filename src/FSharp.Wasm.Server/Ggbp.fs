namespace FSharp.Wasm.Server

/// GGBP — the Ghost-Graph Binary Patch wire format — in the subset fsharp-wasm compiles: no System.IO, no exceptions, byte for byte
/// the format of FSharp.Wasm.Protocol (GhostPatch.encode / decode, which is the reference; tests keep the two identical).
///
///   Magic(1) = 0x47 | Version(4, LE) | TransactionId(4, LE) | OpCount(varint) | Ops
///   UpdateLeaf    0x01 | NodeId(varint) | Field(1) | WasmType(1) | Len(2, LE) | value
///   ReplaceBranch 0x02 | NodeId(varint) | Len(2, LE) | payload
///   ListMutation  0x03 | NodeId(varint) | Field(1) | ListOp(1) | Index(varint) | Len(2, LE) | value
///   DeleteNode    0x04 | NodeId(varint)
module Ggbp =
    [<Literal>]
    let Magic = 0x47uy

    /// The value types of the ghost graph, as the byte that travels.
    module WasmType =
        [<Literal>]
        let I32 = 0uy
        [<Literal>]
        let I64 = 1uy
        [<Literal>]
        let F32 = 2uy
        [<Literal>]
        let F64 = 3uy
        [<Literal>]
        let RefStruct = 4uy
        [<Literal>]
        let RefArray = 5uy
        [<Literal>]
        let RefString = 6uy
        [<Literal>]
        let RefFunc = 7uy
        [<Literal>]
        let RefAny = 8uy

    /// The mutations of a list-typed field, as the byte that travels.
    module ListOp =
        [<Literal>]
        let Insert = 0uy
        [<Literal>]
        let Remove = 1uy
        [<Literal>]
        let Update = 2uy
        [<Literal>]
        let Clear = 3uy

    type PatchOp =
        | UpdateLeaf of nodeId: int * field: int * wasmType: byte * value: byte[]
        | ReplaceBranch of nodeId: int * payload: byte[]
        | ListMutation of nodeId: int * field: int * listOp: byte * index: int * value: byte[]
        | DeleteNode of nodeId: int

    type Patch =
        { Version: uint32
          TransactionId: uint32
          Ops: PatchOp list }

    type DecodeResult =
        | Decoded of Patch
        | InvalidMagic
        | Corrupted of string

    let private writeBlob (w: ByteWriter) (data: byte[]) =
        w.UInt16 data.Length
        w.Bytes data

    let private writeOp (w: ByteWriter) (op: PatchOp) =
        match op with
        | UpdateLeaf(nodeId, field, wasmType, value) ->
            w.Byte 0x01uy
            w.Varint nodeId
            w.Byte(byte field)
            w.Byte wasmType
            writeBlob w value
        | ReplaceBranch(nodeId, payload) ->
            w.Byte 0x02uy
            w.Varint nodeId
            writeBlob w payload
        | ListMutation(nodeId, field, listOp, index, value) ->
            w.Byte 0x03uy
            w.Varint nodeId
            w.Byte(byte field)
            w.Byte listOp
            w.Varint index
            writeBlob w value
        | DeleteNode nodeId ->
            w.Byte 0x04uy
            w.Varint nodeId

    let encode (patch: Patch) : byte[] =
        let w = ByteWriter()
        w.Byte Magic
        w.UInt32 patch.Version
        w.UInt32 patch.TransactionId
        w.Varint(List.length patch.Ops)
        for op in patch.Ops do writeOp w op
        w.ToArray()

    let private readBlob (r: ByteReader) : byte[] =
        let len = r.UInt16()
        r.Bytes len

    /// One operation; None for an unknown tag (the reader is then in an undefined position).
    let private readOp (r: ByteReader) : PatchOp option =
        match int (r.Byte()) with
        | 0x01 ->
            let nodeId = r.Varint()
            let field = int (r.Byte())
            let wasmType = r.Byte()
            Some(UpdateLeaf(nodeId, field, wasmType, readBlob r))
        | 0x02 ->
            let nodeId = r.Varint()
            Some(ReplaceBranch(nodeId, readBlob r))
        | 0x03 ->
            let nodeId = r.Varint()
            let field = int (r.Byte())
            let listOp = r.Byte()
            let index = r.Varint()
            Some(ListMutation(nodeId, field, listOp, index, readBlob r))
        | 0x04 -> Some(DeleteNode(r.Varint()))
        | _ -> None

    let decode (data: byte[]) : DecodeResult =
        if data.Length < 1 then Corrupted "Empty payload"
        elif data.[0] <> Magic then InvalidMagic
        else
            let r = ByteReader data
            r.Byte() |> ignore
            let version = r.UInt32()
            let txn = r.UInt32()
            let count = r.Varint()
            // every operation takes at least one byte: a count beyond the payload is corrupt, and must not allocate
            if r.Failed || count > r.Remaining then Corrupted "Truncated header"
            else
                let ops = ResizeArray<PatchOp>()
                let mutable bad = false
                while not bad && ops.Count < count do
                    match readOp r with
                    | Some op when not r.Failed -> ops.Add op
                    | _ -> bad <- true
                if bad then Corrupted "Invalid operation"
                else Decoded { Version = version; TransactionId = txn; Ops = List.ofSeq ops }

    /// Concatenate the operations; the higher version and the later transaction id win (like GhostPatch.merge).
    let merge (a: Patch) (b: Patch) : Patch =
        { Version = max a.Version b.Version
          TransactionId = b.TransactionId
          Ops = a.Ops @ b.Ops }

    /// FNV-1a of the field name, the key of the field map (FSharp.Wasm.Sdk.FieldMapGenerator).
    let fieldHash (name: string) : uint32 =
        let mutable h = 2166136261u
        for b in Utf8.encode name do
            h <- (h ^^^ uint32 b) * 16777619u
        h
