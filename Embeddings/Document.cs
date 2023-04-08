using System.Text;

namespace GPT.CLI.Embeddings
{
    public class Document
    {
        public string Text { get; set; }
        public double[] Embedding { get; set; }

        public static List<Document> FindMostSimilarDocuments(List<Document> documents, double[] queryEmbedding, int numResults = 3)
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

        public static async Task<List<Document>> ChunkStreamToDocumentsAsync(Stream contentStream, int chunkSize = 2048)
        {
            List<Document> documents = new List<Document>();

            using (StreamReader reader = new StreamReader(contentStream))
            {
                StringBuilder chunkBuilder = new StringBuilder();
                string line;
                int currentChunkSize = 0;

                while ((line = await reader.ReadLineAsync()) != null)
                {
                    int lineLength = line.Length + 1; // +1 for the newline character
                    if (currentChunkSize + lineLength > chunkSize)
                    {
                        documents.Add(new Document { Text = chunkBuilder.ToString() });
                        chunkBuilder.Clear();
                        currentChunkSize = 0;
                    }

                    chunkBuilder.AppendLine(line);
                    currentChunkSize += lineLength;
                }

                if (chunkBuilder.Length > 0)
                {
                    documents.Add(new Document { Text = chunkBuilder.ToString() });
                }
            }

            return documents;
        }
    }
}
