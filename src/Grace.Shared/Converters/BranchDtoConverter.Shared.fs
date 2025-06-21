namespace Grace.Shared.Converters

open Grace.Shared.Utilities
open Grace.Types.Branch
open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Serialization

type BranchDtoConverter() =
    inherit JsonConverter<BranchDto>()

    override this.Read((reader: byref<Utf8JsonReader>), (typeToConvert: Type), (jsonSerializerOptions: JsonSerializerOptions)) : BranchDto =
        let dictionary = Dictionary<string, string>()
        let mutable branchDto = BranchDto.Default

        let t = typeof<BranchDto>
        let fields = t.GetFields()
        let properties = t.GetProperties()
        let members = t.GetMembers()

        logToConsole $"""{sprintf "%A" fields}"""
        logToConsole $"""{sprintf "%A" properties}"""
        logToConsole $"""{sprintf "%A" members}"""

        if reader.TokenType <> JsonTokenType.StartObject then
            raise (JsonException("Invalid JSON text."))
        else
            while reader.Read() do
                if reader.TokenType = JsonTokenType.EndObject then
                    ()
                elif reader.TokenType <> JsonTokenType.PropertyName then
                    ()
                else
                    let key = reader.GetString()
                    ()

                    reader.Read() |> ignore
                    let value: string = reader.GetString()
                    dictionary.Add(key, value)

            for kvp in dictionary do
                logToConsole $"{kvp.Key}: {kvp.Value}"

            branchDto

    override this.Write((writer: Utf8JsonWriter), (value: BranchDto), (jsonSerializerOptions: JsonSerializerOptions)) = ()
