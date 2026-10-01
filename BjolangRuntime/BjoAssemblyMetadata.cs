/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 *
 * As a special exception to the Mozilla Public License, version 2.0, if you
 * compile your application source code and portions of this software are
 * embedded into the generated object code or executable form as a normal
 * consequence of the compilation process (such as inline functions,
 * templates, generics, or macros), you may redistribute such embedded portions
 * in such object code or executable form without complying with the source code
 * availability requirements or notice obligations of Section 3 of the MPL 2.0.
 */

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Bjolang.Runtime;

/// <summary>
/// An assembly's <c>[assembly: AssemblyMetadata(key, value)]</c> entries, read
/// from the file without loading it.
/// </summary>
///
/// <remarks>
/// Loading an assembly to read its attributes keeps it loaded for the life of
/// the process, and a second copy of a module the reader itself links would
/// be refused or shadowed. <c>(janitor docs)</c> reads the docs of any
/// module's dll, so it reads the metadata tables instead. Written here rather
/// than in Bjolang because the reader is handles and a mutable struct.
/// </remarks>
public static class BjoAssemblyMetadata {
    /// <summary>
    /// The value stored under <paramref name="key"/>, or <c>""</c> when the
    /// assembly has none. Throws <c>BadImageFormatException</c> for a file
    /// that is not a .NET assembly, and the usual IO exceptions.
    /// </summary>
    public static string Read(string path, string key) {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) throw new BadImageFormatException($"{path} is not a .NET assembly.");
        var md = pe.GetMetadataReader();

        foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes()) {
            var attribute = md.GetCustomAttribute(handle);
            if (!IsAssemblyMetadata(md, attribute.Constructor)) continue;

            // The blob is the prolog 0x0001 and then the two constructor
            // arguments, each a serialized string.
            var blob = md.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) continue;
            var k = blob.ReadSerializedString();
            var v = blob.ReadSerializedString();
            if (k == key) return v ?? "";
        }

        return "";
    }

    private static bool IsAssemblyMetadata(MetadataReader md, EntityHandle ctor) {
        EntityHandle type;
        switch (ctor.Kind) {
            case HandleKind.MemberReference:
                type = md.GetMemberReference((MemberReferenceHandle)ctor).Parent;
                break;
            case HandleKind.MethodDefinition:
                type = md.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType();
                break;
            default:
                return false;
        }

        return type.Kind switch {
            HandleKind.TypeReference => Named(md, md.GetTypeReference((TypeReferenceHandle)type).Namespace,
                                              md.GetTypeReference((TypeReferenceHandle)type).Name),
            HandleKind.TypeDefinition => Named(md, md.GetTypeDefinition((TypeDefinitionHandle)type).Namespace,
                                               md.GetTypeDefinition((TypeDefinitionHandle)type).Name),
            _ => false,
        };
    }

    private static bool Named(MetadataReader md, StringHandle ns, StringHandle name) =>
        md.StringComparer.Equals(ns, "System.Reflection")
        && md.StringComparer.Equals(name, "AssemblyMetadataAttribute");
}
