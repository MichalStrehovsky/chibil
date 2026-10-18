using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;

namespace Chilink;

internal sealed record PortablePdb(BlobBuilder Image, BlobContentId Id);

internal static class PortablePdbEmitter
{
    public static PortablePdb Emit(MetadataBuilder metadata, IReadOnlyList<SourceToken?> methods,
        IReadOnlyDictionary<SourceToken, DebugMethod> information, MethodDefinitionHandle entryPoint, MetadataMerger merger)
    {
        var pdb = new MetadataBuilder();
        var documents = new Dictionary<DebugDocument, DocumentHandle>();
        DocumentHandle Document(DebugDocument document)
        {
            if (!documents.TryGetValue(document, out var handle))
            {
                handle = pdb.AddDocument(pdb.GetOrAddDocumentName(document.Name),
                    document.HashAlgorithm == Guid.Empty ? default : pdb.GetOrAddGuid(document.HashAlgorithm),
                    document.Checksum.Length == 0 ? default : pdb.GetOrAddBlob(Convert.FromHexString(document.Checksum)),
                    pdb.GetOrAddGuid(new Guid("63a08714-fc37-11d2-904c-00c04fa302a1")));
                documents.Add(document, handle);
            }
            return handle;
        }

        for (int i = 0; i < methods.Count; i++)
        {
            var methodHandle = MetadataTokens.MethodDefinitionHandle(i + 1);
            if (methods[i] is not SourceToken source || !information.TryGetValue(source, out var method))
            {
                pdb.AddMethodDebugInformation(default, default);
                continue;
            }
            if ((MethodDefinitionHandle)merger.Map(method.Definition.Token.Input, method.Definition.Token.Handle) != methodHandle)
                throw new LinkException("PDB method row does not match the selected method body");
            DebugPoint[] points = method.Points.Values.OrderBy(p => p.Offset).ToArray();
            var sequence = new BlobBuilder();
            DocumentHandle singleDocument = default;
            if (points.Length > 0)
            {
                sequence.WriteCompressedInteger(method.LocalSignature.IsNil ? 0 :
                    MetadataTokens.GetRowNumber((StandaloneSignatureHandle)merger.Map(
                        method.Definition.Token.Input, method.LocalSignature)));
                bool single = points.All(p => p.Document == points[0].Document);
                DocumentHandle currentDocument = Document(points[0].Document);
                if (single)
                    singleDocument = currentDocument;
                else
                    sequence.WriteCompressedInteger(MetadataTokens.GetRowNumber(currentDocument));
                int previousOffset = 0, previousLine = 0, previousColumn = 0;
                bool firstNonHidden = true;
                for (int j = 0; j < points.Length; j++)
                {
                    DebugPoint point = points[j];
                    DocumentHandle document = Document(point.Document);
                    if (document != currentDocument)
                    {
                        sequence.WriteCompressedInteger(0);
                        sequence.WriteCompressedInteger(MetadataTokens.GetRowNumber(document));
                        currentDocument = document;
                    }
                    sequence.WriteCompressedInteger(j == 0 ? point.Offset : point.Offset - previousOffset);
                    previousOffset = point.Offset;
                    if (point.Hidden)
                    {
                        sequence.WriteCompressedInteger(0);
                        sequence.WriteCompressedInteger(0);
                        continue;
                    }
                    int lineDelta = point.EndLine - point.StartLine;
                    sequence.WriteCompressedInteger(lineDelta);
                    if (lineDelta == 0)
                        sequence.WriteCompressedInteger(point.EndColumn - point.StartColumn);
                    else
                        sequence.WriteCompressedSignedInteger(point.EndColumn - point.StartColumn);
                    if (firstNonHidden)
                    {
                        sequence.WriteCompressedInteger(point.StartLine);
                        sequence.WriteCompressedInteger(point.StartColumn);
                        firstNonHidden = false;
                    }
                    else
                    {
                        sequence.WriteCompressedSignedInteger(point.StartLine - previousLine);
                        sequence.WriteCompressedSignedInteger(point.StartColumn - previousColumn);
                    }
                    previousLine = point.StartLine;
                    previousColumn = point.StartColumn;
                }
            }
            pdb.AddMethodDebugInformation(singleDocument, points.Length == 0 ? default : pdb.GetOrAddBlob(sequence));
            foreach (DebugScope scope in method.Scopes.OrderBy(s => s.Start).ThenByDescending(s => s.Length))
            {
                var variables = MetadataTokens.LocalVariableHandle(pdb.GetRowCount(TableIndex.LocalVariable) + 1);
                var constants = MetadataTokens.LocalConstantHandle(pdb.GetRowCount(TableIndex.LocalConstant) + 1);
                pdb.AddLocalScope(methodHandle, default, variables, constants, scope.Start, scope.Length);
                foreach (DebugVariable variable in scope.Variables)
                    pdb.AddLocalVariable(LocalVariableAttributes.None, variable.Slot, pdb.GetOrAddString(variable.Name));
            }
        }
        var builder = new PortablePdbBuilder(pdb, metadata.GetRowCounts(), entryPoint, blobs =>
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (Blob blob in blobs)
                hash.AppendData(blob.GetBytes());
            return BlobContentId.FromHash(hash.GetHashAndReset());
        });
        var image = new BlobBuilder();
        return new PortablePdb(image, builder.Serialize(image));
    }
}
