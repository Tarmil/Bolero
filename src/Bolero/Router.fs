// $begin{copyright}
//
// This file is part of Bolero
//
// Copyright (c) 2018 IntelliFactory and contributors
//
// Licensed under the Apache License, Version 2.0 (the "License"); you
// may not use this file except in compliance with the License.  You may
// obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
// implied.  See the License for the specific language governing
// permissions and limitations under the License.
//
// $end{copyright}

namespace Bolero

#nowarn "40" // recursive value `segment` in getSegment

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Text
open FSharp.Reflection

/// A router that binds page navigation with Elmish.
type IRouter<'model, 'msg> =
    /// Get the uri corresponding to `model`.
    abstract GetRoute : model: 'model -> string

    /// Get the message to send when the page navigates to `uri`.
    abstract SetRoute : uri: string -> option<'msg>

/// A simple hand-written router.
type Router<'model, 'msg> =
    {
        /// Get the uri corresponding to `model`.
        getRoute: 'model -> string
        /// Get the message to send when the page navigates to `uri`.
        setRoute: string -> option<'msg>
    }

    interface IRouter<'model, 'msg> with
        member this.GetRoute(model) = this.getRoute model
        member this.SetRoute(uri) = this.setRoute uri

/// A simple router where the endpoint corresponds to a value easily gettable from the model.
type Router<'ep, 'model, 'msg> =
    {
        getEndPoint: 'model -> 'ep
        getRoute: 'ep -> string
        setRoute: string -> option<'msg>
    }

    /// Get the uri for the given endpoint.
    member this.Link(ep) = this.getRoute ep

    interface IRouter<'model, 'msg> with
        member this.GetRoute(model) = this.getRoute (this.getEndPoint model)
        member this.SetRoute(uri) = this.setRoute uri

/// Declare how an F# union case matches to a URI.
[<AttributeUsage(AttributeTargets.Property, AllowMultiple = false)>]
type EndPointAttribute(endpoint: string) =
    inherit Attribute()

    let endpoint = endpoint.Trim('/')

    /// The root path fragment that this endpoint recognizes.
    member this.Root = endpoint

/// Functions for building Routers that bind page navigation with Elmish.
module Router =

    type ArraySegment<'T> with
        member this.Item with get(i) = this.Array.[this.Offset + i]

    type private SegmentParserResult = seq<obj * list<string>>
    type private SegmentParser = list<string> -> SegmentParserResult
    type private SegmentWriter = obj -> list<string>
    type private Segment =
        {
            parse: SegmentParser
            write: SegmentWriter
        }

    let private fail : SegmentParserResult = Seq.empty
    let private ok x : SegmentParserResult = Seq.singleton x

    let inline private tryParseBaseType<'T when 'T : (static member TryParse : string * byref<'T> -> bool)> () =
        fun s ->
            let mutable out = Unchecked.defaultof<'T>
            if (^T : (static member TryParse : string * byref<'T> -> bool) (s, &out)) then
                Some (box out)
            else
                None

    let inline private defaultBaseTypeParser<'T when 'T : (static member TryParse : string * byref<'T> -> bool)> = function
        | [] -> fail
        | x :: rest ->
            match tryParseBaseType<'T>() x with
            | Some x -> ok (box x, rest)
            | None -> fail

    let inline private baseTypeSegment<'T when 'T : (static member TryParse : string * byref<'T> -> bool)> () =
        {
            parse = defaultBaseTypeParser<'T>
            write = fun x -> [string x]
        }

    let private baseTypes : IDictionary<Type, Segment> = dict [
        typeof<string>, {
            parse = function
                | [] -> fail
                | x :: rest -> ok (box x, rest)
            write = unbox<string> >> List.singleton
        }
        typeof<bool>, {
            parse = defaultBaseTypeParser<bool>
            // `string true` returns capitalized "True", but we want lowercase "true".
            write = fun x -> [(if unbox x then "true" else "false")]
        }
        typeof<Byte>, baseTypeSegment<Byte>()
        typeof<SByte>, baseTypeSegment<SByte>()
        typeof<Int16>, baseTypeSegment<Int16>()
        typeof<UInt16>, baseTypeSegment<UInt16>()
        typeof<Int32>, baseTypeSegment<Int32>()
        typeof<UInt32>, baseTypeSegment<UInt32>()
        typeof<Int64>, baseTypeSegment<Int64>()
        typeof<UInt64>, baseTypeSegment<UInt64>()
        typeof<single>, baseTypeSegment<single>()
        typeof<float>, baseTypeSegment<float>()
        typeof<decimal>, baseTypeSegment<decimal>()
    ]

    let private sequenceSegment getSegment (ty: Type) revAndConvert toListAndLength : Segment =
        let itemSegment = getSegment ty
        let rec parse acc remainingLength fragments =
            if remainingLength = 0 then
                ok (revAndConvert acc, fragments)
            else
                itemSegment.parse fragments
                |> Seq.collect (fun (x, rest) ->
                    parse (x :: acc) (remainingLength - 1) rest)
        {
            parse = function
                | x :: rest ->
                    match Int32.TryParse(x) with
                    | true, length -> parse [] length rest
                    | false, _ -> fail
                | _ -> fail
            write = fun x ->
                let list, (length: int) = toListAndLength x
                string length :: List.collect itemSegment.write list
        }

    let [<Literal>] private FLAGS_STATIC =
        Reflection.BindingFlags.Static |||
        Reflection.BindingFlags.Public |||
        Reflection.BindingFlags.NonPublic

    let private arrayRevAndUnbox<'T> (l: list<obj>) : 'T[] =
        let a = [|for x in l -> unbox<'T> x|]
        Array.Reverse(a)
        a

    let private arrayLengthAndBox<'T> (a: array<'T>) : list<obj> * int =
        [for x in a -> box x], a.Length

    let private arraySegment getSegment ty : Segment =
        let arrayRevAndUnbox =
            typeof<Segment>.DeclaringType.GetMethod("arrayRevAndUnbox", FLAGS_STATIC)
                .MakeGenericMethod([|ty|])
        let arrayLengthAndBox =
            typeof<Segment>.DeclaringType.GetMethod("arrayLengthAndBox", FLAGS_STATIC)
                .MakeGenericMethod([|ty|])
        sequenceSegment getSegment ty
            (fun l -> arrayRevAndUnbox.Invoke(null, [|l|]))
            (fun l -> arrayLengthAndBox.Invoke(null, [|l|]) :?> _)

    let private listRevAndUnbox<'T> (l: list<obj>) : list<'T> =
        List.map unbox<'T> l |> List.rev

    let private listLengthAndBox<'T> (l: list<'T>) : list<obj> * int =
        List.mapFold (fun l e -> box e, l + 1) 0 l

    let private listSegment getSegment ty : Segment =
        let listRevAndUnbox =
            typeof<Segment>.DeclaringType.GetMethod("listRevAndUnbox", FLAGS_STATIC)
                .MakeGenericMethod([|ty|])
        let listLengthAndBox =
            typeof<Segment>.DeclaringType.GetMethod("listLengthAndBox", FLAGS_STATIC)
                .MakeGenericMethod([|ty|])
        sequenceSegment getSegment ty
            (fun l -> listRevAndUnbox.Invoke(null, [|l|]))
            (fun l -> listLengthAndBox.Invoke(null, [|l|]) :?> _)

    let private parseEndPointCasePath (case: UnionCaseInfo) =
        case.GetCustomAttributes()
        |> Array.tryPick (function
            | :? EndPointAttribute as e -> Some e.Root
            | _ -> None)
        |> Option.defaultWith (fun () -> case.Name)

    let private parseConsecutiveTypes getSegment (tys: Type[]) (ctor: obj[] -> obj) : SegmentParser =
        let fields = Array.map getSegment tys
        fun (fragments: list<string>) ->
            let args = Array.zeroCreate fields.Length
            let rec go i fragments =
                if i = fields.Length then
                    ok (ctor args, fragments)
                else
                    fields.[i].parse fragments
                    |> Seq.collect (fun (x, rest) ->
                        args.[i] <- x
                        go (i + 1) rest
                    )
            go 0 fragments

    let private writeConsecutiveTypes getSegment (tys: Type[]) (dector: obj -> obj[]) : SegmentWriter =
        let fields = tys |> Array.map (fun t -> (getSegment t).write)
        fun (r: obj) ->
            Array.map2 (<|) fields (dector r)
            |> List.concat

    let private parseUnionCaseArgs getSegment (case: UnionCaseInfo) : SegmentParser =
        let tys = case.GetFields() |> Array.map (fun p -> p.PropertyType)
        let ctor = FSharpValue.PreComputeUnionConstructor case
        parseConsecutiveTypes getSegment tys ctor

    let private writeUnionCase getSegment (path: string) (case: UnionCaseInfo) =
        let tys = case.GetFields() |> Array.map (fun p -> p.PropertyType)
        let dector = FSharpValue.PreComputeUnionReader(case, true)
        let write = writeConsecutiveTypes getSegment tys dector
        match path with
        | "" -> write
        | path -> fun r -> path :: write r

    let private unionSegment (getSegment: Type -> Segment) (ty: Type) : Segment =
        let parsers, readers =
            FSharpType.GetUnionCases(ty, true)
            |> Array.map (fun case ->
                let path = parseEndPointCasePath case
                (path, parseUnionCaseArgs getSegment case), writeUnionCase getSegment path case)
            |> Array.unzip
        let parsers = dict parsers
        let getRoute =
            let tagReader = FSharpValue.PreComputeUnionTagReader(ty, true)
            fun r -> readers.[tagReader r] r
        let unprefixedSetRoute path =
            match parsers.TryGetValue "" with
            | true, c -> c path
            | false, _ -> fail
        let setRoute path =
            seq {
                match path with
                | head :: rest ->
                    match parsers.TryGetValue head with
                    | true, c -> yield! c rest
                    | false, _ -> ()
                | [] -> ()
                yield! unprefixedSetRoute path
            }
        { parse = setRoute; write = getRoute }

    let private tupleSegment getSegment ty =
        let tys = FSharpType.GetTupleElements ty
        let ctor = FSharpValue.PreComputeTupleConstructor ty
        let dector = FSharpValue.PreComputeTupleReader ty
        {
            parse = parseConsecutiveTypes getSegment tys ctor
            write = writeConsecutiveTypes getSegment tys dector
        }

    let private recordSegment getSegment ty =
        let tys = FSharpType.GetRecordFields(ty, true) |> Array.map (fun p -> p.PropertyType)
        let ctor = FSharpValue.PreComputeRecordConstructor(ty, true)
        let dector = FSharpValue.PreComputeRecordReader(ty, true)
        {
            parse = parseConsecutiveTypes getSegment tys ctor
            write = writeConsecutiveTypes getSegment tys dector
        }

    let rec private getSegment (cache: Dictionary<Type, Segment>) (ty: Type) : Segment =
        match cache.TryGetValue(ty) with
        | true, x -> unbox x
        | false, _ ->
            // Add lazy version in case ty is recursive.
            let rec segment = ref {
                parse = fun x -> (!segment).parse x
                write = fun x -> (!segment).write x
            }
            cache.[ty] <- !segment
            let getSegment = getSegment cache
            segment :=
                if ty.IsArray && ty.GetArrayRank() = 1 then
                    arraySegment getSegment (ty.GetElementType())
                elif ty.IsGenericType && ty.GetGenericTypeDefinition() = typedefof<list<_>> then
                    listSegment getSegment (ty.GetGenericArguments().[0])
                elif FSharpType.IsUnion(ty, true) then
                    unionSegment getSegment ty
                elif FSharpType.IsTuple(ty) then
                    tupleSegment getSegment ty
                elif FSharpType.IsRecord(ty, true) then
                    recordSegment getSegment ty
                else
                    failwithf "Router.Infer used with type %s, which is not supported." ty.FullName
            cache.[ty] <- !segment
            !segment

    /// Infer a router constructed around an endpoint type `'ep`.
    /// This type must be an F# union type, and its cases should use `EndPointAttribute`
    /// to declare how they match to a URI.
    let infer<'ep, 'model, 'msg> (makeMessage: 'ep -> 'msg) (getEndPoint: 'model -> 'ep) =
        let ty = typeof<'ep>
        let cache = Dictionary()
        for KeyValue(k, v) in baseTypes do cache.Add(k, v)
        let frag = getSegment cache ty
        {
            getEndPoint = getEndPoint
            getRoute = fun ep ->
                box ep
                |> frag.write
                |> String.concat "/"
            setRoute = fun path ->
                path.Split('/')
                |> List.ofArray
                |> frag.parse
                |> Seq.tryPick (function
                    | x, [] -> Some (unbox<'ep> x |> makeMessage)
                    | _ -> None)
        }

[<Extension>]
type RouterExtensions =

    [<Extension>]
    static member HRef(this: Router<'ep, _, _>, endpoint: 'ep) : Attr =
        Attr("href", this.Link endpoint)
