﻿using System.Text.Json.Serialization;
using OpenAI.GPT3.ObjectModels;
using OpenAI.GPT3.ObjectModels.RequestModels;
using OpenAI.GPT3.ObjectModels.ResponseModels;

namespace GPT.CLI.Chat;

public class ChatBot
{
    public record ChatState
    {
        [JsonPropertyName("parameters")]
        public GPTParameters Parameters { get; set; }

        [JsonPropertyName("messages")]
        public LinkedList<ChatMessage> Messages { get; } = new();

        [JsonPropertyName("instructions")] 
        public readonly List<ChatMessage> Instructions = new();

        [JsonPropertyName("messageLength")]
        public uint MessageLength { get; set; }

        [JsonPropertyName("primeDirective")]
        public ChatMessage PrimeDirective { get; set; } = new(StaticValues.ChatMessageRoles.System,
            "You are a chat bot running in GPT-CLI. Answer every message to the best of your ability.");
    }

    private readonly OpenAILogic _openAILogic;

    [JsonIgnore]
    public ChatState State { get; set; } = new();
    

    public ChatBot(OpenAILogic openAILogic, GPTParameters gptParameters)
    {
        _openAILogic = openAILogic;
        State.Parameters = gptParameters;
    }

    [JsonIgnore]
    public string InstructionStr => State.Instructions.Count > 0 ? string.Join("\n", State.Instructions.Select(x => x.Content)) : string.Empty;


    public async Task AddMessage(ChatMessage message)
    {
        await Console.Out.WriteAsync("Message Added. ");
        while (State.Messages.Count > 0 && State.MessageLength > State.Parameters.MaxChatHistoryLength)
        {
            
            var removed = State.Messages.First();
            var removedLength = removed.Content.Length;

            await Console.Out.WriteAsync($"Length: {State.MessageLength} > Max: {State.Parameters.MaxChatHistoryLength}. Removing top message for: -{removedLength} ");

            State.MessageLength -= (uint)removedLength;
            State.Messages.RemoveFirst();
        }

        if (State.Messages.Count == 0)
        {
            State.MessageLength = 0u;
        }

        State.Messages.AddLast(message);
        State.MessageLength += (uint)message.Content.Length;
        await Console.Out.WriteLineAsync($"Message Length: {State.MessageLength}");
    }

    public void AddInstruction(ChatMessage message)
    {
        State.Instructions.Add(message);
    }


    public async IAsyncEnumerable<ChatCompletionCreateResponse> GetResponseAsync()
    {
        await foreach (var response in _openAILogic.CreateChatCompletionAsyncEnumerable(
                           await ParameterMapping.MapCommon(
                               State.Parameters,
                               _openAILogic, new ChatCompletionCreateRequest()
                               {
                                   Messages = (new List<ChatMessage> {State.PrimeDirective}).Concat(State.Instructions.Concat(State.Messages)).ToList()
                               }, ParameterMapping.Mode.Discord)))
        {
            yield return response;
        }
    }

    public void ClearInstructions()
    {
        State.Instructions.Clear();
    }

    public void ClearMessages()
    {
        State.Messages.Clear();
    }
}