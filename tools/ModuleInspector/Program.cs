using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Lists what an extracted module zip contains, without loading or running any of it: each file and, for .NET DLLs,
// the assemblies they reference, the outside types and methods they use, their P/Invokes and embedded resources.
// Lines starting with "! " are worth a look when reviewing. The output is sorted, so two versions can be diffed
if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: ModuleInspector <extracted module directory>");
    return 1;
}

var root = Path.GetFullPath(args[0]);
var lines = new SortedSet<string>(StringComparer.Ordinal);
foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
{
    var file = Path.GetRelativePath(root, path).Replace('\\', '/');
    var extension = Path.GetExtension(file).ToLowerInvariant();
    lines.Add($"{file}: file");

    if (extension is ".dll" or ".exe")
    {
        Inspect(file, path, lines);
    }
    else if (extension is ".ps1" or ".bat" or ".cmd" or ".vbs" or ".js" or ".msi" or ".sys")
    {
        lines.Add($"! {file}: {extension} file");
    }
}

foreach (var line in lines)
{
    Console.WriteLine(line);
}
return 0;

static void Inspect(string file, string path, SortedSet<string> lines)
{
    using var pe = new PEReader(File.OpenRead(path));
    if (!pe.HasMetadata)
    {
        lines.Add($"! {file}: native code, can't be inspected");
        return;
    }

    var md = pe.GetMetadataReader();
    if (md.IsAssembly)
    {
        var assembly = md.GetAssemblyDefinition();
        lines.Add($"{file}: assembly {md.GetString(assembly.Name)} {assembly.Version}");
    }

    foreach (var handle in md.AssemblyReferences)
    {
        lines.Add($"{file}: references {md.GetString(md.GetAssemblyReference(handle).Name)}");
    }

    foreach (var handle in md.ManifestResources)
    {
        lines.Add($"{file}: resource {md.GetString(md.GetManifestResource(handle).Name)}");
    }

    foreach (var handle in md.MethodDefinitions)
    {
        var method = md.GetMethodDefinition(handle);
        if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0)
        {
            continue;
        }

        var import = method.GetImport();
        var module = import.Module.IsNil ? "?" : md.GetString(md.GetModuleReference(import.Module).Name);
        lines.Add($"! {file}: P/Invoke {module}!{md.GetString(import.Name)}");
    }

    foreach (var handle in md.TypeReferences)
    {
        lines.Add($"{file}: uses type {TypeName(md, handle)}");
    }

    foreach (var handle in md.MemberReferences)
    {
        var reference = md.GetMemberReference(handle);
        var type = ParentTypeName(md, reference.Parent);
        if (type == null)
        {
            continue;
        }

        var member = $"{type}::{md.GetString(reference.Name)}";
        lines.Add($"{(IsNotable(member) ? "! " : "")}{file}: calls {member}");
    }
}

static string TypeName(MetadataReader md, TypeReferenceHandle handle)
{
    var type = md.GetTypeReference(handle);
    var name = md.GetString(type.Name);
    if (type.ResolutionScope.Kind == HandleKind.TypeReference)
    {
        return $"{TypeName(md, (TypeReferenceHandle)type.ResolutionScope)}+{name}";
    }

    var ns = md.GetString(type.Namespace);
    return ns.Length == 0 ? name : $"{ns}.{name}";
}

// The outside type a member belongs to. Null for the module's own types, and arrays and other signatures
static string? ParentTypeName(MetadataReader md, EntityHandle parent)
{
    if (parent.Kind == HandleKind.TypeReference)
    {
        return TypeName(md, (TypeReferenceHandle)parent);
    }
    if (parent.Kind != HandleKind.TypeSpecification)
    {
        return null;
    }

    // A generic instantiation like List<int>. The type arguments are left off
    var signature = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
    if (signature.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance
        || signature.ReadSignatureTypeCode() != SignatureTypeCode.TypeHandle)
    {
        return null;
    }

    var generic = signature.ReadTypeHandle();
    return generic.Kind == HandleKind.TypeReference ? TypeName(md, (TypeReferenceHandle)generic) : null;
}

// Networking, other processes, the registry, the file system, native code, and loading or invoking code by name
static bool IsNotable(string member) => member.StartsWith("System.Net.")
    || member.StartsWith("System.Diagnostics.Process")
    || member.StartsWith("Microsoft.Win32.")
    || member.StartsWith("System.IO.File::")
    || member.StartsWith("System.IO.FileInfo::")
    || member.StartsWith("System.IO.FileStream::")
    || member.StartsWith("System.IO.Directory::")
    || member.StartsWith("System.IO.DirectoryInfo::")
    || member.StartsWith("System.Runtime.InteropServices.Marshal::")
    || member.StartsWith("System.Runtime.InteropServices.NativeLibrary::")
    || member.StartsWith("System.Runtime.Loader.")
    || member.StartsWith("System.Reflection.Emit.")
    || member.StartsWith("System.Reflection.Assembly::Load")
    || member.StartsWith("System.Reflection.MethodBase::Invoke")
    || member == "System.Type::GetType"
    || member.StartsWith("System.Type::InvokeMember")
    || member.StartsWith("System.Activator::CreateInstance");
