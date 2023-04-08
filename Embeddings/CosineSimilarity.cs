using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GPT.CLI.Embeddings;

public static class CosineSimilarity
{
    public static double Calculate(double[] vector1, double[] vector2)
    {
        if (vector1.Length != vector2.Length)
        {
            throw new ArgumentException("Vectors must have the same length.");
        }

        double dotProduct = vector1.Zip(vector2, (a, b) => a * b).Sum();
        double vector1Magnitude = Math.Sqrt(vector1.Sum(a => a * a));
        double vector2Magnitude = Math.Sqrt(vector2.Sum(b => b * b));

        return dotProduct / (vector1Magnitude * vector2Magnitude);
    }
}