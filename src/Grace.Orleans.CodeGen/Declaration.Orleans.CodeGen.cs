// The GenerateCodeForDeclaringAssembly attribute finds all .NET types in the assembly that contains the type you specify - you can pick any
//   type in the assembly, it doesn't matter - and submits them to Orleans code-generation to create serialization code for them. Weird, but it works.

// This line gets everything from Grace.Shared.
[assembly: GenerateCodeForDeclaringAssembly(typeof(Grace.Shared.Types.OwnerType))]

// This line gets everything from Grace.Actors.
[assembly: GenerateCodeForDeclaringAssembly(typeof(Grace.Actors.Owner.OwnerActor))]
