﻿// --------------------------------------------------------------------------------------
// XML type provider - generate code for accessing inferred elements
// --------------------------------------------------------------------------------------
namespace ProviderImplementation

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Xml.Linq
open Microsoft.FSharp.Quotations
open FSharp.Data
open FSharp.Data.Runtime
open FSharp.Data.Runtime.StructuralTypes
open ProviderImplementation
open ProviderImplementation.JsonInference
open ProviderImplementation.ProvidedTypes
open ProviderImplementation.QuotationBuilder

// --------------------------------------------------------------------------------------

#nowarn "10001"

/// Context that is used to generate the XML types.
type internal XmlGenerationContext =
    { CultureStr : string
      TypeProviderType : ProvidedTypeDefinition
      Replacer : AssemblyReplacer
      // to nameclash type names
      UniqueNiceName : string -> string 
      UnifyGlobally : bool
      XmlTypeCache : Dictionary<string, XmlGenerationResult>
      JsonTypeCache : Dictionary<InferedType, ProvidedTypeDefinition> }
    static member Create(cultureStr, tpType, unifyGlobally, replacer) =
        let uniqueNiceName = NameUtils.uniqueGenerator NameUtils.nicePascalName
        uniqueNiceName "XElement" |> ignore
        { CultureStr = cultureStr
          TypeProviderType = tpType
          Replacer = replacer
          UniqueNiceName = uniqueNiceName
          UnifyGlobally = unifyGlobally
          XmlTypeCache = Dictionary()
          JsonTypeCache = Dictionary() }
    member x.ConvertValue prop =
        let typ, _, conv, _ = ConversionsGenerator.convertStringValue x.Replacer "" x.CultureStr prop
        typ, conv

and internal XmlGenerationResult = 
    { ConvertedType : Type
      Converter : Expr -> Expr }

module internal XmlTypeBuilder = 

    /// Recognizes different valid infered types of content:
    ///
    ///  - `Primitive` means that the content is a value and there are no children
    ///  - `Collection` means that there are always just children but no value
    ///  - `Heterogeneous` means that there may be either children or value(s)
    ///
    /// We return a list with all possible primitive types and all possible
    /// children types (both may be empty)
    let (|ContentType|_|) inferedProp = 

        let isListName parentName childName =
            parentName = NameUtils.pluralize childName || parentName = childName + "Array" || parentName = childName + "List"

        match inferedProp with 
        | { Type = (InferedType.Primitive _ | InferedType.Json _) as typ } -> Some([typ], Map.empty)
        | { Type = InferedType.Collection 
                    (SingletonMap (_, (InferedMultiplicity.Single, 
                                       InferedType.Record(Some parentNameWithNS,
                                                          [ { Type = InferedType.Collection (SingletonMap (InferedTypeTag.Record (Some childNameWithNS), 
                                                                                                           (multiplicity,
                                                                                                            InferedType.Record(Some childNameWithNS2, fields, false)))) } ], false)))) } 
          when childNameWithNS = childNameWithNS2 && isListName (XName.Get(parentNameWithNS).LocalName) (XName.Get(childNameWithNS).LocalName) -> 
              let combinedName = Some (parentNameWithNS + "|" + childNameWithNS)
              Some([], Map.add (InferedTypeTag.Record combinedName) (multiplicity, InferedType.Record(combinedName, fields, false)) Map.empty)
        | { Type = InferedType.Collection nodes } -> Some([], nodes)
        | { Type = InferedType.Heterogeneous cases } ->
              let collections, others = Map.toList cases |> List.partition (fst >> ((=) InferedTypeTag.Collection))
              match collections with
              | [InferedTypeTag.Collection, InferedType.Collection nodes] -> Some(List.map snd others, nodes)
              | [] -> Some(List.map snd others, Map.empty)
              | _ -> failwith "(|ContentType|_|): Only one collection type expected"
        // an empty element
        | { Type = InferedType.Top } -> Some([], Map.empty)
        | _ -> None
    
    /// Succeeds when type is a heterogeneous type containing recors
    /// If the type is heterogeneous, but contains other things, exception
    /// is thrown (this is unexpected, because XML nodes are always records)
    let (|HeterogeneousRecords|_|) inferedType =
        match inferedType with
        | InferedType.Heterogeneous cases ->
            let records = 
              cases 
              |> List.ofSeq
              |> List.choose (function 
                  | KeyValue(InferedTypeTag.Record (Some name), v) -> Some(name, v) 
                  | _ -> None)
            if cases.Count = records.Length then Some records
            else failwith "HeterogeneousRecords: Unexpected mix of records and other type kinds"
        | _ -> None
    
    // For every possible primitive type add '<Tag>Value' property that 
    // returns it converted to the right type (or an option)  
    let getTypesForPrimitves (ctx:XmlGenerationContext) forceOptional (primitives:_ list) = [
    
        // If there may be other primitives, make it optional
        let forceOptional = forceOptional || primitives.Length > 1
        
        for primitive in primitives ->
        
            let name = 
                if primitives.Length = 1 
                then "Value" 
                else (StructuralInference.typeTag primitive).NiceName
            
            match primitive with 
            | InferedType.Primitive(typ, unit, optional) -> 
            
                let optional = optional || forceOptional
                let typ, conv = ctx.ConvertValue <| PrimitiveInferedProperty.Create("Value", typ, optional, unit)
                let conv = fun xml -> conv <@ XmlRuntime.TryGetValue(%%xml) @>
                
                typ, name, ctx.Replacer.ToDesignTime >> conv
            
            | InferedType.Json(typ, optional) -> 
            
                let cultureStr = ctx.CultureStr
                let ctx = JsonGenerationContext.Create(cultureStr, ctx.TypeProviderType, ctx.Replacer, ctx.UniqueNiceName, ctx.JsonTypeCache)
                let result = JsonTypeBuilder.generateJsonType ctx (*canPassAllConversionCallingTypes*)false (*optionalityHandledByParent*)true "" typ          
                
                let optional = optional || forceOptional

                let typ = 
                    if optional 
                    then ctx.MakeOptionType result.ConvertedType
                    else result.ConvertedType

                let conv = fun xml ->
                    if optional
                    then <@@ XmlRuntime.TryGetJsonValue(%%xml, cultureStr) @@>
                    else <@@ XmlRuntime.GetJsonValue(%%xml, cultureStr) @@>
                    |> result.GetConverter ctx

                typ, name, ctx.Replacer.ToDesignTime >> conv
            
            | _ -> failwithf "generatePropertiesForValue: Primitive or Json type expected: %A" primitive
    ]
    
    /// Recursively walks over inferred type information and 
    /// generates types for read-only access to the document
    let rec generateXmlType ctx inferedType = 
    
        match inferedType with
       
        // If we already generated object for this type, return it
        | InferedType.Record(Some nameWithNs, _, false) when ctx.XmlTypeCache.ContainsKey nameWithNs -> 
            ctx.XmlTypeCache.[nameWithNs]
        
        // If the node does not have any children and always contains only primitive type
        // then we turn it into a primitive value of type such as int/string/etc.
        | InferedType.Record(Some _, [{ Name = ""
                                        Type = (InferedType.Primitive _ | InferedType.Json _) as primitive }], false) ->
       
            let typ, _, conv = getTypesForPrimitves ctx false [ primitive ] |> Seq.exactlyOne
            { ConvertedType = typ
              Converter = conv }
        | InferedType.Constant(_,t,v)
        | InferedType.Record(Some _, [{ Name = ""
                                        Type = InferedType.Constant(_,t,v)}], false) ->
       
            let conv = fun _ -> <@@ v @@>
            { ConvertedType = t
              Converter = conv }
        // If the node is heterogeneous type containin records, generate type with multiple
        // optional properties (this can only happen when using sample list with multiple root
        // elements of different names). Otherwise, heterogeneous types appear only as child nodes
        // of an element (handled similarly below)
        | HeterogeneousRecords cases ->
       
            // Generate new choice type for the element
            let objectTy = ProvidedTypeDefinition(ctx.UniqueNiceName "Choice", Some(ctx.Replacer.ToRuntime typeof<XmlElement>), HideObjectMethods = true)
            ctx.TypeProviderType.AddMember objectTy
       
            // to nameclash property names
            let makeUnique = NameUtils.uniqueGenerator NameUtils.nicePascalName
            makeUnique "XElement" |> ignore
       
            // For each case, add property of optional type
            objectTy.AddMembers <|
                [ for nameWithNS, case in cases ->
                
                    let result = generateXmlType ctx case
                    let convFunc = ReflectionHelpers.makeDelegate result.Converter (ctx.Replacer.ToRuntime typeof<XmlElement>)
                    ProvidedProperty(makeUnique (XName.Get(nameWithNS).LocalName),
                                     typedefof<option<_>>.MakeGenericType result.ConvertedType,
                                     GetterCode = fun (Singleton xml) ->               
                        // XmlRuntime.ConvertAsName checks that the name of the current node
                        // has the required name and returns Some/None
                        let xmlRuntime = ctx.Replacer.ToRuntime typeof<XmlRuntime>
                        xmlRuntime?ConvertAsName (result.ConvertedType) (xml, nameWithNS, convFunc)) ]
       
            { ConvertedType = objectTy
              Converter = ctx.Replacer.ToRuntime }
       
        // If the node is more complicated, then we generate a type to represent it properly
        | InferedType.Record(Some nameWithNS, props, false) -> 
       
            let names = nameWithNS.Split [| '|' |] |> Array.map (fun nameWithNS -> XName.Get(nameWithNS).LocalName)

            let objectTy = ProvidedTypeDefinition(ctx.UniqueNiceName names.[0],
                                                  Some(ctx.Replacer.ToRuntime typeof<XmlElement>), 
                                                  HideObjectMethods = true)
            ctx.TypeProviderType.AddMember objectTy
       
            // If we unify types globally, then save type for this record
            if ctx.UnifyGlobally then
                ctx.XmlTypeCache.Add(nameWithNS, { ConvertedType = objectTy 
                                                   Converter = ctx.Replacer.ToRuntime })
                
            // Split the properties into attributes and a 
            // special property representing the content
            let attrs, content =
                props |> List.partition (fun prop -> prop.Name <> "")
       
            // to nameclash property names
            let makeUnique = NameUtils.uniqueGenerator NameUtils.nicePascalName
            makeUnique "XElement" |> ignore
       
            // Generate properties for all XML attributes
            for attr in attrs do
       
                let nameWithNS = attr.Name
                let name = XName.Get(nameWithNS).LocalName
                
                let createMemberForAttribute typ unit (optional:bool) =            
                    let typ, conv = ctx.ConvertValue <| PrimitiveInferedProperty.Create("Attribute " + name, typ, optional, unit)
                    objectTy.AddMember <|
                      ProvidedProperty(makeUnique name, typ, GetterCode = fun (Singleton xml) -> 
                          let xml = ctx.Replacer.ToDesignTime xml
                          conv <@ XmlRuntime.TryGetAttribute(%%xml, nameWithNS) @>)
                
                match attr.Type with 
                | InferedType.Heterogeneous types ->
                
                    // If the attribute has multiple possible type (e.g. "bool|int") then we generate
                    // a choice type that is erased to 'option<string>' (for simplicity, assuming that
                    // the attribute is always optional)
                    let choiceTy = ProvidedTypeDefinition(ctx.UniqueNiceName (name + "Choice"), Some(ctx.Replacer.ToRuntime typeof<option<string>>), HideObjectMethods = true)
                    ctx.TypeProviderType.AddMember choiceTy
                
                    // Generate a property for the attribute which has a type 'choiceTy'
                    objectTy.AddMember <|
                        ProvidedProperty(makeUnique name, choiceTy, GetterCode = fun (Singleton xml) -> 
                            let xml = ctx.Replacer.ToDesignTime xml
                            ctx.Replacer.ToRuntime <@@ XmlRuntime.TryGetAttribute(%%xml, nameWithNS) @@>)
                
                    for KeyValue(tag, typ) in types do 
                      
                        if typ.IsOptional then
                            failwithf "generateXmlType: Type shouldn't be optional: %A" typ
                      
                        match typ with
                        | InferedType.Primitive(primTyp, unit, false) ->
                        
                            let typ, conv = ctx.ConvertValue <| PrimitiveInferedProperty.Create(tag.NiceName, primTyp, true, unit)
                            choiceTy.AddMember <|
                                ProvidedProperty(tag.NiceName, typ, GetterCode = fun (Singleton attrVal) -> 
                                    attrVal |> ctx.Replacer.ToDesignTime |> Expr.Cast |> conv)
                        
                        | _ -> failwithf "generateXmlType: A choice type of an attribute can only contain primitive types, got %A" typ
                
                | InferedType.Primitive(typ, unit, optional) -> createMemberForAttribute typ unit optional
                | InferedType.Null -> 
                createMemberForAttribute typeof<string> None false 
                
                | _ -> failwithf "generateXmlType: Expected Primitive or Choice type, got %A" attr.Type
       
            // Add properties that can be used to access content of the node
            // (either child nodes or primitive values - if the node contains simple values)
            match content with 
            | [ContentType(primitives, nodes)] ->
       
                // If there may be other nodes, make it optional
                let forceOptional = nodes.Count > 0
       
                for typ, name, conv in getTypesForPrimitves ctx forceOptional primitives do
                    objectTy.AddMember <| ProvidedProperty(makeUnique name, typ, GetterCode = fun (Singleton xml) -> conv xml)
       
                // For every possible child node, generate 'GetXyz()' method (if there
                // is multiple of them) or just a getter property if there is one or none
                [ for node in nodes ->
       
                    match node with
                    | KeyValue(InferedTypeTag.Record(Some nameWithNS), (multiplicity, typ)) ->
                        
                        let names = nameWithNS.Split [| '|' |] |> Array.map (fun nameWithNS -> XName.Get(nameWithNS).LocalName)
                        let result = generateXmlType ctx typ 
       
                        match multiplicity with
                        | InferedMultiplicity.Single ->
                            ProvidedProperty(makeUnique names.[0], 
                                             result.ConvertedType,
                                             GetterCode = fun (Singleton xml) -> 
                                                 let xml = ctx.Replacer.ToDesignTime xml
                                                 result.Converter <@@ XmlRuntime.GetChild(%%xml, nameWithNS) @@>)
       
                        // For options and arrays, we need to generate call to ConvertArray or ConvertOption
                        // (because the node may be represented as primitive type - so we cannot just
                        // return array of XmlElement - it might be for example int[])
                        | InferedMultiplicity.Multiple ->
                            let convFunc = ReflectionHelpers.makeDelegate result.Converter (ctx.Replacer.ToRuntime typeof<XmlElement>)
                            ProvidedProperty(makeUnique (NameUtils.pluralize names.[0]), 
                                             result.ConvertedType.MakeArrayType(),
                                             GetterCode = fun (Singleton xml) -> 
                                                 let xmlRuntime = ctx.Replacer.ToRuntime typeof<XmlRuntime>
                                                 xmlRuntime?ConvertArray (result.ConvertedType) (xml, nameWithNS, convFunc))
       
                        | InferedMultiplicity.OptionalSingle ->

                            let convFunc = ReflectionHelpers.makeDelegate result.Converter (ctx.Replacer.ToRuntime typeof<XmlElement>)
                            if result.ConvertedType.Name.StartsWith "FSharpOption`1" then
                                ProvidedProperty(makeUnique names.[0], 
                                                 result.ConvertedType,
                                                 GetterCode = fun (Singleton xml) -> 
                                                     let xmlRuntime = ctx.Replacer.ToRuntime typeof<XmlRuntime>
                                                     xmlRuntime?ConvertOptional2 (result.ConvertedType.GenericTypeArguments.[0]) (xml, nameWithNS, convFunc))
                            else
                              ProvidedProperty(makeUnique names.[0], 
                                               typedefof<option<_>>.MakeGenericType result.ConvertedType,
                                               GetterCode = fun (Singleton xml) -> 
                                                   let xmlRuntime = ctx.Replacer.ToRuntime typeof<XmlRuntime>
                                                   xmlRuntime?ConvertOptional (result.ConvertedType) (xml, nameWithNS, convFunc))
       
                    | _ -> failwithf "generateXmlType: Child nodes should be named record types, got %A" node ]
       
                |> objectTy.AddMembers 
       
            | [_] -> failwithf "generateXmlType: Children should be collection or heterogeneous: %A" content
            | _::_ -> failwithf "generateXmlType: Only one child collection expected: %A" content
            | [] -> ()
       
            { ConvertedType = objectTy 
              Converter = ctx.Replacer.ToRuntime }
       
        | _ -> failwithf "generateXmlType: Infered type should be record type: %A" inferedType
