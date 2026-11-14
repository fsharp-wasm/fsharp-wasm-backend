namespace FSharp.Wasm.Server

open System.Text

/// JSON, written out so that nothing depends on System.Text.Json: a value type, a writer and a parser for what an API of this size
/// exchanges.  Numbers are doubles; the parser takes standard JSON (strings with \uXXXX escapes, nested arrays and objects) and
/// gives None for anything else, with no exceptions.
type Json =
    | JNull
    | JBool of bool
    | JNum of float
    | JStr of string
    | JArr of Json list
    | JObj of (string * Json) list

module Json =
    let private hexDigit (n: int) = "0123456789abcdef".[n &&& 0xF]

    let private writeString (sb: StringBuilder) (s: string) =
        sb.Append('"') |> ignore
        for c in s do
            match c with
            | '"' -> sb.Append("\\\"") |> ignore
            | '\\' -> sb.Append("\\\\") |> ignore
            | '\n' -> sb.Append("\\n") |> ignore
            | '\r' -> sb.Append("\\r") |> ignore
            | '\t' -> sb.Append("\\t") |> ignore
            | c when int c < 0x20 ->
                sb.Append("\\u00").Append(hexDigit (int c >>> 4)).Append(hexDigit (int c)) |> ignore
            | c -> sb.Append(c) |> ignore
        sb.Append('"') |> ignore

    let rec private write (sb: StringBuilder) (v: Json) =
        match v with
        | JNull -> sb.Append("null") |> ignore
        | JBool true -> sb.Append("true") |> ignore
        | JBool false -> sb.Append("false") |> ignore
        | JNum n ->
            // integers print without a fraction; JSON has no NaN or Infinity
            if not (Numbers.isFinite n) then sb.Append("null") |> ignore
            elif n = System.Math.Floor n && abs n < 1e15 then sb.Append(string (int64 n)) |> ignore
            else sb.Append(string n) |> ignore
        | JStr s -> writeString sb s
        | JArr items ->
            sb.Append('[') |> ignore
            let mutable first = true
            for item in items do
                if not first then sb.Append(',') |> ignore
                first <- false
                write sb item
            sb.Append(']') |> ignore
        | JObj fields ->
            sb.Append('{') |> ignore
            let mutable first = true
            for (key, value) in fields do
                if not first then sb.Append(',') |> ignore
                first <- false
                writeString sb key
                sb.Append(':') |> ignore
                write sb value
            sb.Append('}') |> ignore

    let stringify (v: Json) : string =
        let sb = StringBuilder()
        write sb v
        sb.ToString()

    /// The parser keeps its position and a failure flag; a failure anywhere makes the whole parse None.
    type private Parser(s: string) =
        let mutable pos = 0
        let mutable failed = false

        member _.Failed = failed
        member _.AtEnd = pos >= s.Length
        member _.Fail() =
            failed <- true
            JNull
        member _.SkipWhitespace() =
            while pos < s.Length && (s.[pos] = ' ' || s.[pos] = '\n' || s.[pos] = '\r' || s.[pos] = '\t') do
                pos <- pos + 1
        member _.Peek : char = if pos < s.Length then s.[pos] else '\000'
        member _.Advance() = pos <- pos + 1
        member _.Literal(word: string) : bool =
            if pos + word.Length <= s.Length && s.Substring(pos, word.Length) = word then
                pos <- pos + word.Length
                true
            else false
        member _.Hex4() : int =
            if pos + 4 > s.Length then
                failed <- true
                0
            else
                let mutable v = 0
                for i in 0 .. 3 do
                    let c = s.[pos + i]
                    let d =
                        if c >= '0' && c <= '9' then int c - int '0'
                        elif c >= 'a' && c <= 'f' then int c - int 'a' + 10
                        elif c >= 'A' && c <= 'F' then int c - int 'A' + 10
                        else -1
                    if d < 0 then failed <- true else v <- v * 16 + d
                pos <- pos + 4
                v
        member this.String() : string =
            // the opening quote is at pos
            pos <- pos + 1
            let sb = StringBuilder()
            let mutable closed = false
            while not closed && not failed do
                if pos >= s.Length then failed <- true
                else
                    let c = s.[pos]
                    pos <- pos + 1
                    if c = '"' then closed <- true
                    elif c = '\\' then
                        if pos >= s.Length then failed <- true
                        else
                            let e = s.[pos]
                            pos <- pos + 1
                            match e with
                            | '"' -> sb.Append('"') |> ignore
                            | '\\' -> sb.Append('\\') |> ignore
                            | '/' -> sb.Append('/') |> ignore
                            | 'n' -> sb.Append('\n') |> ignore
                            | 'r' -> sb.Append('\r') |> ignore
                            | 't' -> sb.Append('\t') |> ignore
                            | 'b' -> sb.Append('\b') |> ignore
                            | 'f' -> sb.Append('\012') |> ignore
                            | 'u' -> sb.Append(char (this.Hex4())) |> ignore
                            | _ -> failed <- true
                    elif int c < 0x20 then failed <- true
                    else sb.Append(c) |> ignore
            sb.ToString()
        member _.Number() : Json =
            let start = pos
            if pos < s.Length && s.[pos] = '-' then pos <- pos + 1
            while pos < s.Length && ((s.[pos] >= '0' && s.[pos] <= '9') || s.[pos] = '.' || s.[pos] = 'e' || s.[pos] = 'E' || s.[pos] = '+' || s.[pos] = '-') do
                pos <- pos + 1
            let text = s.Substring(start, pos - start)
            match Numbers.parseFloat text with
            | Some n -> JNum n
            | None ->
                failed <- true
                JNull

    let rec private parseValue (p: Parser) (depth: int) : Json =
        p.SkipWhitespace()
        if depth > 64 then p.Fail()
        else
            match p.Peek with
            | '"' -> JStr(p.String())
            | '{' ->
                p.Advance()
                let fields = ResizeArray<string * Json>()
                p.SkipWhitespace()
                if p.Peek = '}' then p.Advance()
                else
                    let mutable more = true
                    while more && not p.Failed do
                        p.SkipWhitespace()
                        if p.Peek <> '"' then more <- false; p.Fail() |> ignore
                        else
                            let key = p.String()
                            p.SkipWhitespace()
                            if p.Peek <> ':' then more <- false; p.Fail() |> ignore
                            else
                                p.Advance()
                                fields.Add((key, parseValue p (depth + 1)))
                                p.SkipWhitespace()
                                if p.Peek = ',' then p.Advance()
                                elif p.Peek = '}' then p.Advance(); more <- false
                                else more <- false; p.Fail() |> ignore
                JObj(List.ofSeq fields)
            | '[' ->
                p.Advance()
                let items = ResizeArray<Json>()
                p.SkipWhitespace()
                if p.Peek = ']' then p.Advance()
                else
                    let mutable more = true
                    while more && not p.Failed do
                        items.Add(parseValue p (depth + 1))
                        p.SkipWhitespace()
                        if p.Peek = ',' then p.Advance()
                        elif p.Peek = ']' then p.Advance(); more <- false
                        else more <- false; p.Fail() |> ignore
                JArr(List.ofSeq items)
            | 't' -> if p.Literal "true" then JBool true else p.Fail()
            | 'f' -> if p.Literal "false" then JBool false else p.Fail()
            | 'n' -> if p.Literal "null" then JNull else p.Fail()
            | c when c = '-' || (c >= '0' && c <= '9') -> p.Number()
            | _ -> p.Fail()

    let parse (text: string) : Json option =
        let p = Parser text
        let v = parseValue p 0
        p.SkipWhitespace()
        if p.Failed || not p.AtEnd then None else Some v

    /// The value of a field of an object.
    let field (name: string) (v: Json) : Json option =
        match v with
        | JObj fields -> fields |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd
        | _ -> None

    let asString (v: Json option) : string option =
        match v with
        | Some(JStr s) -> Some s
        | _ -> None

    let asNumber (v: Json option) : float option =
        match v with
        | Some(JNum n) -> Some n
        | _ -> None

    let asBool (v: Json option) : bool option =
        match v with
        | Some(JBool b) -> Some b
        | _ -> None
