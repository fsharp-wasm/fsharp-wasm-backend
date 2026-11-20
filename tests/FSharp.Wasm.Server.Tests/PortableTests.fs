/// The portable layer (FSharp.Wasm.Server) against what it replaces: the .NET reference of FSharp.Wasm.Protocol for the GGBP wire
/// format, System.Text for UTF-8 and numbers.  The portable code is what runs as WebAssembly; these tests are what says it means
/// the same thing as the code it was written beside.
module PortableTests

open System
open System.Text
open Xunit
open FSharp.Wasm.Data
open FSharp.Wasm.Server
open FSharp.Wasm.Server.Ggbp

// ── the GGBP wire format ─────────────────────────────────────────────────────

/// A deterministic pseudo-random patch (seeded), covering all four operations and the sizes around the varint and blob limits.
let private randomPatch (rnd: Random) : Patch =
    let bytes (n: int) = Array.init n (fun _ -> byte (rnd.Next 256))
    let node () = [| 0; 1; 127; 128; 300; 16383; 16384; 2097151; 2097152; 268435455; rnd.Next(Int32.MaxValue) |].[rnd.Next 11]
    let op () =
        match rnd.Next 4 with
        | 0 -> UpdateLeaf(node (), rnd.Next 256, byte (rnd.Next 9), bytes (rnd.Next 40))
        | 1 -> ReplaceBranch(node (), bytes ([| 0; 1; 255; 256; 1000; 65535 |].[rnd.Next 6]))
        | 2 -> ListMutation(node (), rnd.Next 256, byte (rnd.Next 4), node (), bytes (rnd.Next 20))
        | _ -> DeleteNode(node ())
    { Version = uint32 (rnd.NextInt64 4294967296L)
      TransactionId = uint32 (rnd.NextInt64 4294967296L)
      Ops = List.init (rnd.Next 12) (fun _ -> op ()) }

/// The same patch as the reference library's types.
let private toReference (p: Patch) : FSharp.Wasm.Protocol.Patch =
    let open_ = FSharp.Wasm.Protocol.PatchOp.UpdateLeaf
    { Magic = FSharp.Wasm.Protocol.GhostPatch.MagicByte
      Version = FSharp.Wasm.Protocol.ModelVersion p.Version
      TransactionId = FSharp.Wasm.Protocol.TransactionId p.TransactionId
      Ops =
        p.Ops
        |> List.map (function
            | UpdateLeaf(n, f, t, v) -> open_ (FSharp.Wasm.Protocol.NodeId n, FSharp.Wasm.Protocol.FieldIndex(byte f), LanguagePrimitives.EnumOfValue<byte, FSharp.Wasm.Protocol.WasmType> t, v)
            | ReplaceBranch(n, b) -> FSharp.Wasm.Protocol.PatchOp.ReplaceBranch(FSharp.Wasm.Protocol.NodeId n, b)
            | ListMutation(n, f, l, i, v) ->
                FSharp.Wasm.Protocol.PatchOp.ListMutation(FSharp.Wasm.Protocol.NodeId n, FSharp.Wasm.Protocol.FieldIndex(byte f), LanguagePrimitives.EnumOfValue<byte, FSharp.Wasm.Protocol.ListOp> l, i, v)
            | DeleteNode n -> FSharp.Wasm.Protocol.PatchOp.DeleteNode(FSharp.Wasm.Protocol.NodeId n)) }

[<Fact>]
let ``encoding gives the bytes of the .NET reference, for 2000 random patches`` () =
    let rnd = Random 20260114
    for _ in 1..2000 do
        let patch = randomPatch rnd
        let expected = FSharp.Wasm.Protocol.GhostPatch.encode (toReference patch)
        Assert.Equal<byte[]>(expected, encode patch)

[<Fact>]
let ``what the reference encodes, the portable decoder reads`` () =
    let rnd = Random 7
    for _ in 1..500 do
        let patch = randomPatch rnd
        match decode (FSharp.Wasm.Protocol.GhostPatch.encode (toReference patch)) with
        | Decoded decoded -> Assert.Equal(patch, decoded)
        | other -> failwithf "not decoded: %A" other

[<Fact>]
let ``decode (encode p) = p`` () =
    let rnd = Random 11
    for _ in 1..500 do
        let patch = randomPatch rnd
        Assert.Equal(Decoded patch, decode (encode patch))

[<Fact>]
let ``the reference decoder reads what the portable encoder writes`` () =
    let rnd = Random 13
    for _ in 1..300 do
        let patch = randomPatch rnd
        match FSharp.Wasm.Protocol.GhostPatch.decode (encode patch) with
        | FSharp.Wasm.Protocol.PatchResult.Ok p -> Assert.Equal(patch.Ops.Length, p.Ops.Length)
        | other -> failwithf "the reference did not decode: %A" other

[<Fact>]
let ``decoding bad input is a result, not an exception`` () =
    Assert.Equal(Corrupted "Empty payload", decode [||])
    Assert.Equal(InvalidMagic, decode [| 0x48uy; 0uy; 0uy |])
    match decode [| Magic; 1uy; 0uy |] with Corrupted _ -> () | other -> failwithf "%A" other      // truncated header
    // an operation count far beyond the payload must not allocate or loop: 0xFFFFFFF operations in 12 bytes
    match decode [| Magic; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0xFFuy; 0xFFuy; 0xFFuy; 0x7Fuy |] with Corrupted _ -> () | other -> failwithf "%A" other
    // an unknown operation tag, and an operation cut off in the middle
    match decode [| Magic; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 1uy; 0x7Fuy |] with Corrupted _ -> () | other -> failwithf "%A" other
    match decode [| Magic; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 1uy; 0x01uy; 5uy; 0uy; 0uy; 9uy; 0uy |] with Corrupted _ -> () | other -> failwithf "%A" other

[<Fact>]
let ``every truncation of a patch is Corrupted or a shorter patch, never an exception`` () =
    let rnd = Random 17
    for _ in 1..60 do
        let bytes = encode (randomPatch rnd)
        for n in 0 .. bytes.Length - 1 do
            match decode bytes.[0 .. n - 1] with
            | Decoded _ | Corrupted _ | InvalidMagic -> ()

[<Fact>]
let ``merge takes the higher version and the later transaction`` () =
    let a = { Version = 5u; TransactionId = 1u; Ops = [ DeleteNode 1 ] }
    let b = { Version = 4u; TransactionId = 2u; Ops = [ DeleteNode 2 ] }
    Assert.Equal({ Version = 5u; TransactionId = 2u; Ops = [ DeleteNode 1; DeleteNode 2 ] }, merge a b)

[<Fact>]
let ``the field hash is the FNV-1a of the UTF-8 name, like the SDK`` () =
    for name in [ ""; "id"; "name"; "Größe"; "日本語"; "🚀" ] do
        Assert.Equal(FSharp.Wasm.Sdk.FieldMapGenerator.hashFieldName name, fieldHash name)

// ── bytes, UTF-8, hex ────────────────────────────────────────────────────────

[<Fact>]
let ``a varint round-trips, and takes as many bytes as LEB128 does`` () =
    for v in [ 0; 1; 127; 128; 16383; 16384; 2097151; 2097152; 268435455; 268435456; Int32.MaxValue ] do
        let w = ByteWriter()
        w.Varint v
        let bytes = w.ToArray()
        Assert.Equal<byte[]>(FSharp.Wasm.Protocol.GhostPatch.encodeVarint v, bytes)
        let r = ByteReader bytes
        Assert.Equal(v, r.Varint())
        Assert.False r.Failed

[<Fact>]
let ``a reader that runs out sets Failed instead of raising`` () =
    let r = ByteReader [| 1uy |]
    Assert.Equal(1uy, r.Byte())
    Assert.False r.Failed
    Assert.Equal(0uy, r.Byte())
    Assert.True r.Failed
    let r2 = ByteReader [| 0x80uy; 0x80uy; 0x80uy; 0x80uy; 0x80uy; 0x80uy; 0x01uy |]
    r2.Varint() |> ignore
    Assert.True r2.Failed                       // a varint of more than five bytes is corrupt
    let r3 = ByteReader [| 1uy; 2uy |]
    Assert.Empty(r3.Bytes 3)
    Assert.True r3.Failed

[<Theory>]
[<InlineData("")>]
[<InlineData("plain ASCII")>]
[<InlineData("äöüß ÄÖÜ")>]
[<InlineData("€ ✓ 日本語")>]
[<InlineData("🚀 and 𝔘𝔫𝔦𝔠𝔬𝔡𝔢")>]
let ``UTF-8 is what System.Text gives, and decodes back`` (s: string) =
    let expected = Encoding.UTF8.GetBytes s
    Assert.Equal<byte[]>(expected, Utf8.encode s)
    Assert.Equal(s, Utf8.decode expected)

[<Fact>]
let ``UTF-8 of malformed input is U+FFFD, never an exception`` () =
    Assert.Equal("\uFFFD", Utf8.decode [| 0xFFuy |])
    Assert.Equal("a\uFFFDb", Utf8.decode [| byte 'a'; 0xC3uy; byte 'b' |])        // a lead byte without its continuation
    Assert.Equal("\uFFFD\uFFFD", Utf8.decode [| 0xE2uy; 0x82uy |])                  // a three-byte sequence cut off after two
    Assert.Equal("\uFFFD", Utf8.decode [| 0x80uy |])                                 // a stray continuation byte

[<Fact>]
let ``hex round-trips and rejects what is not hex`` () =
    let rnd = Random 5
    for _ in 1..100 do
        let bytes = Array.init (rnd.Next 50) (fun _ -> byte (rnd.Next 256))
        Assert.Equal(Some bytes, Hex.decode (Hex.encode bytes))
    Assert.Equal("00ff10", Hex.encode [| 0uy; 255uy; 16uy |])
    Assert.Equal(Some [| 171uy |], Hex.decode "AB")
    Assert.Equal(None, Hex.decode "abc")
    Assert.Equal(None, Hex.decode "zz")

// ── numbers ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``parseInt takes what Int32 can hold and nothing else`` () =
    Assert.Equal(Some 0, Numbers.parseInt "0")
    Assert.Equal(Some 42, Numbers.parseInt "42")
    Assert.Equal(Some -7, Numbers.parseInt "-7")
    Assert.Equal(Some Int32.MaxValue, Numbers.parseInt "2147483647")
    Assert.Equal(Some Int32.MinValue, Numbers.parseInt "-2147483648")
    for bad in [ ""; "-"; "+1"; " 1"; "1 "; "1.5"; "0x10"; "2147483648"; "-2147483649"; "99999999999999999999"; "abc" ] do
        Assert.Equal(None, Numbers.parseInt bad)

[<Fact>]
let ``parseUInt32 takes digits up to 4294967295`` () =
    Assert.Equal(Some 0u, Numbers.parseUInt32 "0")
    Assert.Equal(Some 4294967295u, Numbers.parseUInt32 "4294967295")
    for bad in [ ""; "-1"; "4294967296"; "12345678901"; "1a"; " 1" ] do
        Assert.Equal(None, Numbers.parseUInt32 bad)

[<Fact>]
let ``parseFloat agrees with Double.Parse on JSON numbers`` () =
    for text in [ "0"; "-0"; "1"; "-1"; "3.14"; "-2.5"; "1e3"; "1E3"; "1e-3"; "2.5e+2"; "123456789"; "0.1"; "100.125"; "1.5e10" ] do
        let expected = Double.Parse(text, Globalization.CultureInfo.InvariantCulture)
        match Numbers.parseFloat text with
        | Some v -> Assert.True(abs (v - expected) <= abs expected * 1e-12, sprintf "%s: %g <> %g" text v expected)
        | None -> failwithf "%s did not parse" text
    for bad in [ ""; "-"; "."; "1."; ".5"; "1e"; "1e+"; "--1"; "1.2.3"; "abc"; "0x10"; " 1" ] do
        Assert.Equal(None, Numbers.parseFloat bad)

[<Fact>]
let ``isFinite is false for NaN and the infinities`` () =
    Assert.True(Numbers.isFinite 0.0)
    Assert.True(Numbers.isFinite -1.5e300)
    Assert.False(Numbers.isFinite nan)
    Assert.False(Numbers.isFinite infinity)
    Assert.False(Numbers.isFinite -infinity)

// ── JSON ────────────────────────────────────────────────────────────────────

[<Fact>]
let ``JSON text round-trips through stringify and parse`` () =
    let value =
        JObj [ "text", JStr "line\nbreak \"quoted\" \\ tab\t ünï ✓ 🚀 \u0001"
               "n", JNum 42.0
               "pi", JNum 3.25
               "neg", JNum -7.0
               "flags", JArr [ JBool true; JBool false; JNull ]
               "nested", JObj [ "empty", JArr []; "o", JObj [] ] ]
    Assert.Equal(Some value, Json.tryParse (Json.stringify value))

[<Fact>]
let ``JSON is what System.Text.Json reads`` () =
    let text = Json.stringify (JObj [ "a", JStr "é ✓ 🚀 \"x\""; "b", JArr [ JNum 1.0; JNum 2.5 ] ])
    use doc = System.Text.Json.JsonDocument.Parse text
    Assert.Equal("é ✓ 🚀 \"x\"", doc.RootElement.GetProperty("a").GetString())
    Assert.Equal(2.5, doc.RootElement.GetProperty("b").[1].GetDouble())

[<Fact>]
let ``the parser reads standard JSON, with escapes and surrogate pairs`` () =
    Assert.Equal(Some(JStr "é"), Json.tryParse "\"\\u00e9\"")
    Assert.Equal(Some(JStr "🚀"), Json.tryParse "\"\\ud83d\\ude80\"")
    Assert.Equal(Some(JObj [ "k", JArr [ JNum 1.0; JNum 2.0 ] ]), Json.tryParse " { \"k\" : [ 1 , 2 ] } ")
    Assert.Equal(Some(JNum 1500.0), Json.tryParse "1.5e3")

[<Fact>]
let ``the parser gives None for what is not JSON, and never raises`` () =
    for bad in [ ""; "{"; "}"; "[1,"; "{\"a\"}"; "{\"a\":}"; "{a:1}"; "[1 2]"; "\"unterminated"; "\"bad \\q escape\""; "tru"; "nul"; "01x"; "{\"a\":1} extra"; "\"\\u12\""; "\"tab\there\"" ] do
        Assert.Equal(None, Json.tryParse bad)
    // nesting deeper than the limit is refused instead of overflowing the stack
    Assert.Equal(None, Json.tryParse (String('[', 1000) + String(']', 1000)))

[<Fact>]
let ``field and the accessors`` () =
    let v = (Json.tryParse "{\"text\":\"hi\",\"n\":3,\"ok\":true}").Value
    Assert.Equal(Some "hi", Json.asString (Json.field "text" v))
    Assert.Equal(Some 3.0, Json.asNumber (Json.field "n" v))
    Assert.Equal(Some true, Json.asBool (Json.field "ok" v))
    Assert.Equal(None, Json.asString (Json.field "missing" v))
    Assert.Equal(None, Json.asNumber (Json.field "text" v))

// ── HTTP helpers ─────────────────────────────────────────────────────────────

[<Fact>]
let ``segments drop empty parts`` () =
    Assert.Equal<string list>([ "api"; "todos"; "3" ], Http.segments "/api/todos/3")
    Assert.Equal<string list>([ "a" ], Http.segments "//a//")
    Assert.Equal<string list>([], Http.segments "/")

[<Fact>]
let ``headers are matched without regard to case`` () =
    let req = { Method = "GET"; Path = "/"; Query = [ "since", "3" ]; Headers = [ "X-Transaction", "9" ]; Body = "" }
    Assert.Equal(Some "9", Http.header "x-transaction" req)
    Assert.Equal(Some "3", Http.query "since" req)
    Assert.Equal(None, Http.query "nope" req)
