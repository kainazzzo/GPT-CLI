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

        [JsonPropertyName("is-image")]
        public bool IsImage { get; set; }

        [JsonPropertyName("source-filename")]
        public string SourceFileName { get; set; }

        [JsonPropertyName("stored-path")]
        public string StoredFilePath { get; set; }

        [JsonPropertyName("source-message-id")]
        public ulong SourceMessageId { get; set; }

        public static List<Document> LoadEmbeddings(Stream input)
        {
            try
            {
                return JsonSerializer.Deserialize<List<Document>>(input);
            }
            catch
            {
                return new();
            }
        }

        public static IEnumerable<(Document Document, double Similarity)> FindMostSimilarDocuments(List<Document> documents, List<double> queryEmbedding, int numResults)
        {
            var similarities = new List<(Document Document, double Similarity)>();

            // Calculate the cosine similarity between the query and each document
            foreach (var document in documents)
            {
                double similarity = CosineSimilarity.Calculate(queryEmbedding, document.Embedding);
                similarities.Add((document, similarity));
            }

            // Sort by similarity
            similarities.Sort((x, y) => y.Similarity.CompareTo(x.Similarity));
            
            // Return the top numResults
            return similarities.Take(numResults);
        }

        

        public static async Task<List<Document>> ChunkToDocumentsAsync(Stream contentStream, int chunkSize)
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

        public static async Task<List<Document>> ChunkToDocumentsAsync(string content, int chunkSize)
        {
            using var contentStream = new MemoryStream(Encoding.UTF8.GetBytes(content));

            return await ChunkToDocumentsAsync(contentStream, chunkSize);
        }
    }
}
