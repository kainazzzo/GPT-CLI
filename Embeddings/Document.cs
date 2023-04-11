using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GPT.CLI.Embeddings
{
    public class Document
    {
        [JsonPropertyName("text")]
        public string Text { get; set; }

        [JsonPropertyName("embed")]
        public List<double> Embedding { get; set; }

        public static List<Document> LoadEmbeddings(Stream input)
        {
            try
            {
                return JsonSerializer.Deserialize<List<Document>>(input);
            }
            catch
            {
                return null;
            }
        }

        public static List<Document> FindMostSimilarDocuments(List<Document> documents, List<double> queryEmbedding, int numResults = 3)
        {
            var similarities = new List<(Document Document, double Similarity)>();

            foreach (var document in documents)
            {
                double similarity = CosineSimilarity.Calculate(queryEmbedding, document.Embedding);
                similarities.Add((document, similarity));
            }

            similarities.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
            return similarities.Take(numResults).Select(x => x.Document).ToList();
        }

        public static async Task<List<Document>> ChunkStreamToDocumentsAsync(Stream contentStream, int chunkSize = 512)
        {
            var documents = new List<Document>();

            var buffer = new byte[chunkSize];
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, chunkSize)) > 0)
            {
                documents.Add(new Document { Text = Encoding.UTF8.GetString(buffer, 0, bytesRead) });
            }
            

            return documents;
        }
    }
}
