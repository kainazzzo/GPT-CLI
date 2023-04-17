namespace GPT.CLI
{
    public static class AsyncEnumerableExtensions
    {
        public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this List<T> list)
        {
            foreach (var item in list)
            {
                await Task.Yield(); // Yield the execution to avoid blocking the thread.
                yield return item;
            }
        }

        public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this T item)
        {
            await Task.Yield();
            yield return item;
        }
    }
}
