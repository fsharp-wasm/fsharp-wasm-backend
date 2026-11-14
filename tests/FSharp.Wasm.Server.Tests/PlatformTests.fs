/// The platforms the backend modules run on, as data: the enumerations of the SDK and the host, the capability table, the config.
module PlatformTests

open Xunit
open FSharp.Wasm.Host

[<Theory>]
[<InlineData("cloudflare", "Cloudflare")>]
[<InlineData("vercel", "Vercel")>]
[<InlineData("netlify", "Netlify")>]
[<InlineData("deno", "Deno")>]
[<InlineData("bun", "Bun")>]
[<InlineData("node", "NodeJs")>]
[<InlineData("nodejs", "NodeJs")>]
[<InlineData("fastly", "Fastly")>]
[<InlineData("something-else", "Generic")>]
let ``the project config names the platforms`` (text: string, expected: string) =
    let config = FSharp.Wasm.Sdk.ProjectConfig.fromJson (sprintf "{ \"platform\": \"%s\" }" text)
    Assert.Equal(expected, sprintf "%A" config.Platform)

[<Fact>]
let ``platforms that import a module precompiled need the Exports string ABI`` () =
    Assert.True(PlatformCapabilities.precompiledModule PlatformKind.Cloudflare)
    Assert.True(PlatformCapabilities.precompiledModule PlatformKind.Vercel)
    Assert.False(PlatformCapabilities.precompiledModule PlatformKind.Deno)
    Assert.False(PlatformCapabilities.precompiledModule PlatformKind.NodeJs)
    for kind in [ PlatformKind.Cloudflare; PlatformKind.Vercel; PlatformKind.Netlify; PlatformKind.Deno; PlatformKind.Bun; PlatformKind.NodeJs ] do
        Assert.Equal(StringAbi.Exports, PlatformCapabilities.stringAbi kind)

[<Fact>]
let ``every platform with a working adapter names its directory`` () =
    Assert.Equal(Some "cloudflare", PlatformCapabilities.adapterDirectory PlatformKind.Cloudflare)
    Assert.Equal(Some "vercel", PlatformCapabilities.adapterDirectory PlatformKind.Vercel)
    Assert.Equal(Some "netlify", PlatformCapabilities.adapterDirectory PlatformKind.Netlify)
    Assert.Equal(Some "node", PlatformCapabilities.adapterDirectory PlatformKind.NodeJs)
    // WASI platforms need components: no adapter of this kind
    Assert.Equal(None, PlatformCapabilities.adapterDirectory PlatformKind.Fastly)
    Assert.Equal(None, PlatformCapabilities.adapterDirectory PlatformKind.Fermyon)

[<Fact>]
let ``a Durable Object, a server process and a Deno or Bun instance are stateful; Vercel and Netlify instances are not`` () =
    for kind in [ PlatformKind.Cloudflare; PlatformKind.Deno; PlatformKind.Bun; PlatformKind.NodeJs ] do
        Assert.True(PlatformCapabilities.statefulInstance kind)
    Assert.False(PlatformCapabilities.statefulInstance PlatformKind.Vercel)
    Assert.False(PlatformCapabilities.statefulInstance PlatformKind.Netlify)
