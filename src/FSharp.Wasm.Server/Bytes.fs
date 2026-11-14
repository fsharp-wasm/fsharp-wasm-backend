namespace FSharp.Wasm.Server

open System.Text

/// A growable byte buffer: the portable replacement of MemoryStream + BinaryWriter (little-endian, like BinaryWriter).
type ByteWriter() =
    let bytes = ResizeArray<byte>()

    member _.Length = bytes.Count
    member _.Byte(b: byte) = bytes.Add b
    member this.UInt16(v: int) =
        this.Byte(byte (v &&& 0xFF))
        this.Byte(byte ((v >>> 8) &&& 0xFF))
    member this.UInt32(v: uint32) =
        this.Byte(byte (v &&& 0xFFu))
        this.Byte(byte ((v >>> 8) &&& 0xFFu))
        this.Byte(byte ((v >>> 16) &&& 0xFFu))
        this.Byte(byte ((v >>> 24) &&& 0xFFu))
    /// Unsigned LEB128 of a non-negative integer.
    member this.Varint(value: int) =
        let mutable v = value
        if v <= 0 then this.Byte 0uy
        else
            while v > 0 do
                let low = byte (v &&& 0x7F)
                v <- v >>> 7
                this.Byte(if v > 0 then low ||| 0x80uy else low)
    member this.Bytes(data: byte[]) =
        for b in data do this.Byte b
    member _.ToArray() : byte[] = bytes.ToArray()

/// Reads what ByteWriter wrote.  Out-of-range reads give 0 and set `Failed` instead of raising: a decoder checks the flag once.
type ByteReader(data: byte[]) =
    let mutable pos = 0
    let mutable failed = false

    member _.Position = pos
    member _.Remaining = data.Length - pos
    member _.Failed = failed
    member _.Byte() : byte =
        if pos < data.Length then
            let b = data.[pos]
            pos <- pos + 1
            b
        else
            failed <- true
            0uy
    member this.UInt16() : int =
        let lo = int (this.Byte())
        let hi = int (this.Byte())
        lo ||| (hi <<< 8)
    member this.UInt32() : uint32 =
        let b0 = uint32 (this.Byte())
        let b1 = uint32 (this.Byte())
        let b2 = uint32 (this.Byte())
        let b3 = uint32 (this.Byte())
        b0 ||| (b1 <<< 8) ||| (b2 <<< 16) ||| (b3 <<< 24)
    /// Unsigned LEB128; more than five bytes is corrupt.
    member this.Varint() : int =
        let mutable result = 0
        let mutable shift = 0
        let mutable more = true
        while more do
            let b = this.Byte()
            result <- result ||| (int (b &&& 0x7Fuy) <<< shift)
            shift <- shift + 7
            more <- (b &&& 0x80uy) <> 0uy && not failed
            if shift > 35 then
                failed <- true
                more <- false
        result
    member this.Bytes(count: int) : byte[] =
        if count < 0 || count > data.Length - pos then
            failed <- true
            pos <- data.Length
            [||]
        else
            let out = Array.zeroCreate<byte> count
            for i in 0 .. count - 1 do out.[i] <- data.[pos + i]
            pos <- pos + count
            out

/// UTF-8 of .NET strings (UTF-16 code units), written out so that nothing depends on System.Text.Encoding.
module Utf8 =
    let encode (s: string) : byte[] =
        let w = ByteWriter()
        let mutable i = 0
        while i < s.Length do
            let c = int s.[i]
            if c < 0x80 then w.Byte(byte c)
            elif c < 0x800 then
                w.Byte(byte (0xC0 ||| (c >>> 6)))
                w.Byte(byte (0x80 ||| (c &&& 0x3F)))
            elif c >= 0xD800 && c <= 0xDBFF && i + 1 < s.Length && int s.[i + 1] >= 0xDC00 && int s.[i + 1] <= 0xDFFF then
                let cp = 0x10000 + ((c - 0xD800) <<< 10) + (int s.[i + 1] - 0xDC00)
                w.Byte(byte (0xF0 ||| (cp >>> 18)))
                w.Byte(byte (0x80 ||| ((cp >>> 12) &&& 0x3F)))
                w.Byte(byte (0x80 ||| ((cp >>> 6) &&& 0x3F)))
                w.Byte(byte (0x80 ||| (cp &&& 0x3F)))
                i <- i + 1
            else
                w.Byte(byte (0xE0 ||| (c >>> 12)))
                w.Byte(byte (0x80 ||| ((c >>> 6) &&& 0x3F)))
                w.Byte(byte (0x80 ||| (c &&& 0x3F)))
            i <- i + 1
        w.ToArray()

    /// Malformed sequences become U+FFFD.
    let decode (bytes: byte[]) : string =
        let sb = StringBuilder()
        let n = bytes.Length
        let cont (k: int) = k < n && (int bytes.[k] &&& 0xC0) = 0x80
        let mutable i = 0
        while i < n do
            let b = int bytes.[i]
            if b < 0x80 then
                sb.Append(char b) |> ignore
                i <- i + 1
            elif b >= 0xC2 && b < 0xE0 && cont (i + 1) then
                sb.Append(char (((b &&& 0x1F) <<< 6) ||| (int bytes.[i + 1] &&& 0x3F))) |> ignore
                i <- i + 2
            elif b >= 0xE0 && b < 0xF0 && cont (i + 1) && cont (i + 2) then
                sb.Append(char (((b &&& 0x0F) <<< 12) ||| ((int bytes.[i + 1] &&& 0x3F) <<< 6) ||| (int bytes.[i + 2] &&& 0x3F))) |> ignore
                i <- i + 3
            elif b >= 0xF0 && b < 0xF5 && cont (i + 1) && cont (i + 2) && cont (i + 3) then
                let cp = ((b &&& 0x07) <<< 18) ||| ((int bytes.[i + 1] &&& 0x3F) <<< 12) ||| ((int bytes.[i + 2] &&& 0x3F) <<< 6) ||| (int bytes.[i + 3] &&& 0x3F)
                let v = cp - 0x10000
                sb.Append(char (0xD800 + (v >>> 10))).Append(char (0xDC00 + (v &&& 0x3FF))) |> ignore
                i <- i + 4
            else
                sb.Append(char 0xFFFD) |> ignore
                i <- i + 1
        sb.ToString()

/// Lower-case hexadecimal, the form in which binary patches travel inside JSON.
module Hex =
    let private digits = "0123456789abcdef"

    let encode (bytes: byte[]) : string =
        let sb = StringBuilder()
        for b in bytes do
            sb.Append(digits.[int b >>> 4]).Append(digits.[int b &&& 0xF]) |> ignore
        sb.ToString()

    let private nibble (c: char) : int =
        if c >= '0' && c <= '9' then int c - int '0'
        elif c >= 'a' && c <= 'f' then int c - int 'a' + 10
        elif c >= 'A' && c <= 'F' then int c - int 'A' + 10
        else -1

    /// None when the text has an odd length or a character that is not a hex digit.
    let decode (s: string) : byte[] option =
        if s.Length % 2 <> 0 then None
        else
            let out = Array.zeroCreate<byte> (s.Length / 2)
            let mutable ok = true
            for i in 0 .. out.Length - 1 do
                let hi = nibble s.[2 * i]
                let lo = nibble s.[2 * i + 1]
                if hi < 0 || lo < 0 then ok <- false else out.[i] <- byte ((hi <<< 4) ||| lo)
            if ok then Some out else None
