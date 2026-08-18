using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using KoboldSharp;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

namespace NovelAIEndpoint;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.silverpine.novelai-endpoint";
    public const string PluginName = "NovelAI Endpoint";
    public const string PluginVersion = "1.2.6";

    internal static ManualLogSource Log = null!;
    internal static ConfigEntry<bool> UseNovelAI = null!;
    internal static ConfigEntry<string> Model = null!;
    internal static ConfigEntry<float> RequestDelaySeconds = null!;
    internal static ConfigEntry<bool> SendStopSequences = null!;
    internal static ConfigEntry<string> EncryptedApiKey = null!;
    internal static ConfigEntry<bool> SeparateKeyMigrationComplete = null!;
    private static ConfigFile PluginConfig = null!;

    private void Awake()
    {
        Log = Logger;
        PluginConfig = Config;
        UseNovelAI = Config.Bind(
            "Endpoint",
            "Use NovelAI",
            false,
            "Selects NovelAI instead of OpenRouter for Silverpine's hosted API mode.");
        Model = Config.Bind(
            "Endpoint",
            "Model",
            "xialong-v1",
            "NovelAI text-generation model ID.");
        if (Model.Value == "kayra-v1")
            Model.Value = "xialong-v1";
        RequestDelaySeconds = Config.Bind(
            "Endpoint",
            "Delay Between Requests",
            1f,
            "Minimum cooldown in seconds after one NovelAI request completes " +
            "before the next queued request begins.");
        SendStopSequences = Config.Bind(
            "Endpoint",
            "Send Stop Sequences",
            true,
            "Sends a bounded set of Silverpine stop sequences to NovelAI " +
            "text-completion requests. Streaming chat requests enforce " +
            "their stops locally.");
        EncryptedApiKey = Config.Bind(
            "Authentication",
            "Encrypted API Key",
            "",
            "NovelAI access token encrypted with Silverpine's " +
            "SimpleEncryption format. Enter the token through the game's " +
            "API key field while NovelAI is selected; do not enter plaintext " +
            "in this configuration value.");
        SeparateKeyMigrationComplete = Config.Bind(
            "Authentication",
            "Separate Key Migration Complete",
            false,
            "Internal migration marker that prevents an OpenRouter key from " +
            "being mistaken for a NovelAI token.");

        new Harmony(PluginGuid).PatchAll();
        Log.LogInfo("NovelAI endpoint loaded.");
    }

    internal static string GetNovelAIApiKey()
    {
        string encrypted = EncryptedApiKey.Value?.Trim() ?? "";
        if (encrypted.Length == 0)
            return "";

        try
        {
            return SimpleEncryption.SimpleDecrypt(encrypted).Trim();
        }
        catch (Exception exception)
        {
            Log.LogError(
                "The encrypted NovelAI API key could not be decrypted. " +
                $"Re-enter it in the game settings. {exception.Message}");
            return "";
        }
    }

    internal static void SaveNovelAIApiKey(string token)
    {
        string trimmed = token?.Trim() ?? "";
        string encrypted = trimmed.Length == 0
            ? ""
            : SimpleEncryption.SimpleEncrypt(trimmed);
        if (EncryptedApiKey.Value == encrypted)
            return;

        EncryptedApiKey.Value = encrypted;
        PluginConfig.Save();
    }
}

internal static class ProviderApiKeyManager
{
    private static TMP_InputField ApiKeyInputField = null!;
    private static string OpenRouterApiKey = "";

    internal static void Attach(SettingsUI settings)
    {
        ApiKeyInputField = Traverse.Create(settings)
            .Field("apiKeyInputField")
            .GetValue<TMP_InputField>();
        ApiKeyInputField.onValueChanged.AddListener(value =>
        {
            if (!Plugin.UseNovelAI.Value)
            {
                // Silverpine's listener runs first and has already updated its
                // own plaintext-in-memory OpenRouter value.
                OpenRouterApiKey = settings.apiKey ?? "";
                return;
            }

            Plugin.SaveNovelAIApiKey(value);

            // Silverpine's original listener temporarily copied the visible
            // field into apiKey and saved it. Restore the OpenRouter value and
            // immediately rewrite settings.json so the providers stay separate.
            settings.apiKey = OpenRouterApiKey;
            SaveSilverpineConfig(settings);
        });
    }

    internal static void AfterSettingsLoaded(SettingsUI settings)
    {
        OpenRouterApiKey = settings.apiKey ?? "";

        if (!Plugin.SeparateKeyMigrationComplete.Value)
        {
            // Versions through 1.2.5 reused Silverpine's OpenRouter field. If
            // NovelAI was the saved provider, move that value once, then clear
            // it from settings.json so it cannot be sent to OpenRouter later.
            if (Plugin.UseNovelAI.Value &&
                string.IsNullOrEmpty(Plugin.GetNovelAIApiKey()) &&
                !string.IsNullOrWhiteSpace(settings.apiKey))
            {
                Plugin.SaveNovelAIApiKey(settings.apiKey);
                settings.apiKey = "";
                OpenRouterApiKey = "";
                SaveSilverpineConfig(settings);
                Plugin.Log.LogInfo(
                    "Migrated the existing NovelAI token into the plugin's " +
                    "encrypted provider-specific configuration.");
            }

            Plugin.SeparateKeyMigrationComplete.Value = true;
        }

        ShowSelectedProviderKey(Plugin.UseNovelAI.Value);
    }

    internal static void OnProviderChanged(
        SettingsUI settings,
        bool novelAISelected)
    {
        if (novelAISelected)
        {
            OpenRouterApiKey = settings.apiKey ?? "";
        }
        else
        {
            settings.apiKey = OpenRouterApiKey;
        }

        ShowSelectedProviderKey(novelAISelected);
    }

    internal static void PrepareNovelAI(SettingsUI settings, string token)
    {
        OpenRouterApiKey = settings.apiKey ?? "";
        Plugin.SaveNovelAIApiKey(token);
        ShowSelectedProviderKey(novelAISelected: true);
    }

    private static void ShowSelectedProviderKey(bool novelAISelected)
    {
        if (ApiKeyInputField == null)
            return;

        ApiKeyInputField.SetTextWithoutNotify(
            novelAISelected
                ? Plugin.GetNovelAIApiKey()
                : OpenRouterApiKey);
    }

    private static void SaveSilverpineConfig(SettingsUI settings)
    {
        Traverse.Create(settings).Method("SaveConfig").GetValue();
    }
}

[HarmonyPatch(typeof(SettingsUI), "Awake")]
internal static class SettingsUIAwakePatch
{
    private const string NovelAILabel = "NovelAI";

    private static void Postfix(SettingsUI __instance)
    {
        try
        {
            MineButton selector = Traverse.Create(__instance)
                .Field("aiProcessingMineButton")
                .GetValue<MineButton>();

            AddNovelAIOption(selector);
            ProviderApiKeyManager.Attach(__instance);
            selector.OnValueChanged += value =>
            {
                bool selected = value == NovelAILabel;
                Plugin.UseNovelAI.Value = selected;
                if (selected)
                {
                    __instance.useAIServer = false;
                    __instance.useAPIModel = true;
                    __instance.apiUrl = NovelAIClient.BaseUrl;
                }
                else
                {
                    __instance.apiUrl = "https://openrouter.ai/api/v1";
                }
                ProviderApiKeyManager.OnProviderChanged(
                    __instance,
                    selected);
            };

        }
        catch (Exception exception)
        {
            Plugin.Log.LogError($"Could not add NovelAI endpoint: {exception}");
        }
    }

    private static void AddNovelAIOption(MineButton selector)
    {
        Traverse selectorFields = Traverse.Create(selector);
        IList options = selectorFields.Field("options").GetValue<IList>();
        Type optionType = typeof(MineButton).GetNestedType(
            "Option",
            BindingFlags.NonPublic)!;
        FieldInfo labelField = AccessTools.Field(optionType, "label");

        foreach (object option in options)
        {
            if ((string)labelField.GetValue(option) == NovelAILabel)
                return;
        }

        object novelAIOption = Activator.CreateInstance(optionType)!;
        labelField.SetValue(novelAIOption, NovelAILabel);

        // Reuse the OpenRouter option's visibility rules so the API-key control
        // appears for NovelAI as well.
        FieldInfo enableField = AccessTools.Field(optionType, "toEnable");
        foreach (object option in options)
        {
            if ((string)labelField.GetValue(option) == "OpenRouter")
            {
                var enabledObjects =
                    (List<GameObject>)enableField.GetValue(option);
                enableField.SetValue(
                    novelAIOption,
                    new List<GameObject>(enabledObjects));
                break;
            }
        }

        options.Add(novelAIOption);
    }
}

[HarmonyPatch(typeof(SettingsUI), "Start")]
internal static class SettingsUIStartPatch
{
    private static void Postfix(SettingsUI __instance)
    {
        try
        {
            ProviderApiKeyManager.AfterSettingsLoaded(__instance);
            if (!Plugin.UseNovelAI.Value)
                return;

            __instance.useAIServer = false;
            __instance.useAPIModel = true;
            __instance.apiUrl = NovelAIClient.BaseUrl;

            MineButton selector = Traverse.Create(__instance)
                .Field("aiProcessingMineButton")
                .GetValue<MineButton>();

            // Silverpine loads and redraws its settings in Start. Restore the
            // provider only after that load, without firing SaveConfig.
            selector.SetValueWithoutNotify("NovelAI");
        }
        catch (Exception exception)
        {
            Plugin.Log.LogError(
                $"Could not restore the NovelAI selection: {exception}");
        }
    }
}

[HarmonyPatch(typeof(APIModelHandler), nameof(APIModelHandler.GetChatCompletion))]
internal static class ChatCompletionPatch
{
    private static bool Prefix(
        GenParams genParams,
        ref Task<APIModelHandler.ChatCompletionsReponse> __result)
    {
        if (!Plugin.UseNovelAI.Value)
            return true;

        __result = NovelAIClient.Generate(genParams);
        return false;
    }
}

[HarmonyPatch(
    typeof(DialogBox),
    "AnimateText",
    new[] { typeof(string), typeof(bool), typeof(Action), typeof(bool) })]
internal static class NovelAIDialogFormattingPatch
{
    private static void Prefix(ref string text)
    {
        if (!Plugin.UseNovelAI.Value || string.IsNullOrEmpty(text))
            return;

        // Empty action spans such as "Gareth: ** ** *He looks up.*" render
        // as an expanding blank gap. Remove them before Silverpine interprets
        // the asterisks as formatting.
        text = Regex.Replace(
            text,
            @"(?m)(^|\n)([^\r\n:]{1,80}:(?:\*)?[ \t]*)?(?:\*\*(?:[ \t]+|$))+",
            "$1$2");

        // Silverpine normally changes every adjacent action boundary from
        // "* *" to "* - *". Merge those action spans before its animator
        // can manufacture visible, cumulative dash separators.
        text = Regex.Replace(text, @"\*\s+\*", " ");

        // Clean separators already stored in an active conversation. This is
        // display-only; it does not rewrite the user's save data.
        text = Regex.Replace(
            text,
            @"^([^\r\n:]{1,80}:\s*)(?:[-\u2013\u2014]\s*)+",
            "$1");
    }
}

[HarmonyPatch(
    typeof(DialogBox),
    nameof(DialogBox.DisplayTextNoDialog),
    new[] { typeof(string), typeof(DialogOption[]) })]
internal static class EndpointChoiceDialogPatch
{
    private const string ChoicePrompt =
        "Please choose which option the game should use for AI processing.";

    private static void Prefix(string text, ref DialogOption[] dialogOptions)
    {
        if (text != ChoicePrompt)
            return;

        foreach (DialogOption option in dialogOptions)
        {
            if (option.label == "Use NovelAI")
                return;
        }

        var expanded = new List<DialogOption>(dialogOptions)
        {
            new("Use NovelAI", SelectNovelAI)
        };
        dialogOptions = expanded.ToArray();
    }

    private static void SelectNovelAI()
    {
        void Start(string token)
        {
            SettingsUI settings = SettingsUI.Instance;
            ProviderApiKeyManager.PrepareNovelAI(settings, token);
            settings.apiUrl = NovelAIClient.BaseUrl;
            settings.useAIServer = false;
            settings.useAPIModel = true;
            Plugin.UseNovelAI.Value = true;

            Traverse.Create(settings).Method("SaveConfig").GetValue();
            InferenceServerSetupHandler handler =
                InferenceServerSetupHandler.Instance;
            Traverse.Create(handler).Method("StartAPIMode").GetValue();
        }

        string existingToken = Plugin.GetNovelAIApiKey();
        if (!string.IsNullOrWhiteSpace(existingToken))
        {
            Start(existingToken);
            return;
        }

        TextInputUI.Instance.Open(
            "Enter NovelAI Access Token",
            value => !string.IsNullOrWhiteSpace(value),
            Start,
            allowClosing: true);
    }
}

internal static class NovelAIClient
{
    internal const string BaseUrl = "https://text.novelai.net/oa/v1";
    private static readonly string[] SentenceEndStopSequences =
        { ".", "!", "?" };
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static readonly HashSet<string> ValidatedModels =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> ChatUnsupportedModels =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedRoutes =
        new(StringComparer.Ordinal);
    private static string LastValidationFailureModel = "";
    private static DateTime LastValidationFailureUtc = DateTime.MinValue;
    private static DateTime LastRequestCompletedUtc = DateTime.MinValue;
    private const string CompatibilityInstructions =
        "Every named character is a separate person. Never transfer " +
        "appearance, species, anatomy, possessions, memories, " +
        "relationships, or personality traits between characters. " +
        "Attribute a detail only to the character explicitly named with " +
        "that detail. When uncertain, omit only that detail instead of " +
        "guessing, but always provide the requested answer.\n" +
        "A dialogue request is exactly one turn for exactly one active NPC. " +
        "Write only that NPC's response. Never write dialogue, actions, " +
        "reactions, decisions, feelings, or inner thoughts for the player or " +
        "any other character, even when they appear elsewhere in the " +
        "context. Other characters are context only: the active NPC may " +
        "address or refer to them, but must not control them or advance their " +
        "turn. Stop when the active NPC's turn is complete. Do not begin with " +
        "any character name or role label.\n" +
        "Never use underscores for emphasis or formatting. In dialogue, " +
        "spoken words must remain plain text outside asterisks. Put complete " +
        "physical actions and appropriate narrative action or scene " +
        "descriptions inside asterisks, such as Hello there. *She turns " +
        "toward the door.* Never put spoken dialogue inside asterisks, and " +
        "never present an action as spoken dialogue. Do not use asterisks " +
        "merely to emphasize individual words or pronouns. Every action " +
        "sentence, including the first sentence of the first reply, must " +
        "have its own opening and closing asterisk. If a reply begins with " +
        "an action, the very first non-whitespace character must be an " +
        "asterisk, for example: *She looks up.* Hello there.";
    private const string UtilityInstructions =
        "Follow the user's requested output format exactly. Return only the " +
        "requested answer, with no role label, explanation, commentary, or " +
        "Markdown code fence.";

    [Serializable]
    private sealed class CompletionRequest
    {
        public string model = "";
        public string prompt = "";
        public int max_tokens = 512;
        public float temperature = 0.3f;
        public int top_k = 64;
        public float top_p = 1f;
        public string[] stop = Array.Empty<string>();
    }

    [Serializable]
    private sealed class ChatRequest
    {
        public string model = "";
        public List<Message> messages = new();
        public int max_tokens = 512;
        public float temperature = 0.3f;
        public int top_k = 64;
        public float top_p = 1f;
        public int n = 1;
        public bool echo = false;
        public bool enable_thinking = false;
        public bool stream = true;
    }

    [Serializable]
    private sealed class ModelsResponse
    {
        public List<ModelInfo> data = new();
    }

    [Serializable]
    private sealed class ModelInfo
    {
        public string id = "";
    }

    [Serializable]
    private sealed class Message
    {
        public string role = "";
        public string content = "";
    }

    [Serializable]
    private sealed class Response
    {
        public List<Choice> choices = new();
    }

    [Serializable]
    private sealed class Choice
    {
        public string text = "";
        public ResponseMessage message = new();
        public ResponseMessage delta = new();
        public string finish_reason = "";
        public string matched_stop = "";
    }

    [Serializable]
    private sealed class ResponseMessage
    {
        public string content = "";
    }

    private sealed class NovelAIRequestException : Exception
    {
        internal long ResponseCode { get; }

        internal NovelAIRequestException(long responseCode, string message)
            : base(message)
        {
            ResponseCode = responseCode;
        }
    }

    internal static async Task<APIModelHandler.ChatCompletionsReponse> Generate(
        GenParams genParams)
    {
        await RequestGate.WaitAsync();
        try
        {
            double cooldown = Math.Max(
                0.0,
                Plugin.RequestDelaySeconds.Value);
            double elapsed = (
                DateTime.UtcNow - LastRequestCompletedUtc).TotalSeconds;
            double remaining = cooldown - elapsed;
            if (remaining > 0.0)
            {
                await Task.Delay(TimeSpan.FromSeconds(remaining));
            }
            return await GenerateCore(genParams);
        }
        finally
        {
            LastRequestCompletedUtc = DateTime.UtcNow;
            RequestGate.Release();
        }
    }

    private static async Task<APIModelHandler.ChatCompletionsReponse> GenerateCore(
        GenParams genParams)
    {
        if (string.IsNullOrWhiteSpace(Plugin.GetNovelAIApiKey()))
            throw new Exception(
                "Enter your NovelAI access token in the API Key field.");

        await EnsureConfiguredModelAvailable();

        List<Message> messages = ConvertMessages(genParams.prompt);
        bool hasAssistantPrefill = messages.Count > 0 &&
            messages[messages.Count - 1].role == "assistant";
        string model = Plugin.Model.Value.Trim();
        string completionPrompt = BuildCompletionPrompt(
            messages,
            genParams.isDialog,
            genParams.grammar);
        List<Message> chatMessages = BuildChatMessages(
            messages,
            genParams.isDialog,
            genParams.grammar);
        bool isConversationBroadcast = IsConversationBroadcast(genParams);

        string[] silverpineStopSequences = (genParams.stop_sequence ??
                Array.Empty<string>())
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct()
            .ToArray();
        string[] stopSequences = isConversationBroadcast
            ? SentenceEndStopSequences
                .Concat(silverpineStopSequences)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : silverpineStopSequences;

        string[] serverStopSequences = Plugin.SendStopSequences.Value
            ? BuildServerStopSequences(stopSequences)
            : Array.Empty<string>();
        int maxTokens = genParams.max_length > 0
            ? genParams.max_length
            : 512;
        float temperature = Mathf.Max(0.1f, genParams.temperature);
        int topK = genParams.top_k == 0 ? 64 : genParams.top_k;
        float topP = genParams.top_p <= 0f ? 1f : genParams.top_p;

        var completionPayload = new CompletionRequest
        {
            model = model,
            prompt = completionPrompt,
            max_tokens = maxTokens,
            temperature = temperature,
            top_k = topK,
            top_p = topP,
            stop = serverStopSequences
        };
        var chatPayload = new ChatRequest
        {
            model = model,
            messages = chatMessages,
            max_tokens = maxTokens,
            temperature = temperature,
            top_k = topK,
            top_p = topP
        };

        // Exact assistant prefixes (letters, summaries, labels, and other
        // continuation tasks) need the text-completion endpoint. Ordinary
        // role-based requests use native chat so the model receives real
        // system/user/assistant boundaries.
        bool useChatEndpoint = !hasAssistantPrefill &&
            !ChatUnsupportedModels.Contains(model);
        LogRouteOnce(model, useChatEndpoint);

        async Task<(Choice choice, string rawResponse)> SendSelected()
        {
            if (!useChatEndpoint)
                return await SendWithStopFallback(completionPayload);

            try
            {
                return await SendChat(chatPayload);
            }
            catch (NovelAIRequestException exception)
                when (IsChatCompatibilityFailure(exception.ResponseCode))
            {
                ChatUnsupportedModels.Add(model);
                useChatEndpoint = false;
                Plugin.Log.LogWarning(
                    $"NovelAI's chat endpoint rejected model '{model}' " +
                    $"(HTTP {exception.ResponseCode}; " +
                    $"{GetSafeErrorDetail(exception)}). " +
                    $"Message layout: {DescribeMessageLayout(chatMessages)}. " +
                    "Falling back to text completions for this model for " +
                    "the rest of this game session.");
                LogRouteOnce(model, false);
                return await SendWithStopFallback(completionPayload);
            }
        }

        void AddRetryCue(string cue)
        {
            completionPayload.prompt +=
                "\n\nUSER:\n" + cue + "\n\nASSISTANT:\n";

            if (chatPayload.messages.Count > 0 &&
                chatPayload.messages[chatPayload.messages.Count - 1].role ==
                    "user")
            {
                chatPayload.messages[chatPayload.messages.Count - 1].content +=
                    "\n\n" + cue;
            }
            else
            {
                chatPayload.messages.Add(new Message
                {
                    role = "user",
                    content = cue
                });
            }
        }

        Choice choice;
        string rawResponse;
        try
        {
            (choice, rawResponse) = await SendSelected();
        }
        catch
        {
            Plugin.Log.LogError(
                "NovelAI rejected a request with " +
                $"the {(useChatEndpoint ? "chat" : "completion")} endpoint, " +
                $"{messages.Count} converted messages, " +
                $"{stopSequences.Length} local stop sequences, " +
                $"prefill length {GetPrefillLength(messages)}, " +
                $"and prompt length {genParams.prompt?.Length ?? 0}.");
            throw;
        }

        string output = GetChoiceText(choice);
        bool retried = false;
        if (string.IsNullOrEmpty(output))
        {
            Plugin.Log.LogWarning(
                "NovelAI returned an empty successful response; " +
                "retrying once with an explicit answer cue.");
            double retryDelay = Math.Max(
                0.0,
                Plugin.RequestDelaySeconds.Value);
            if (retryDelay > 0.0)
                await Task.Delay(TimeSpan.FromSeconds(retryDelay));

            AddRetryCue(
                "Provide the requested answer now. " +
                "Do not stop without answering. Return only the answer, " +
                "without a USER, ASSISTANT, or SYSTEM label.");
            (choice, rawResponse) = await SendSelected();
            retried = true;
            output = GetChoiceText(choice);
            if (string.IsNullOrEmpty(output))
            {
                Plugin.Log.LogError(
                    "NovelAI response contained no text after one retry. " +
                    $"Finish reason: {choice.finish_reason ?? "(null)"}, " +
                    $"matched stop present: " +
                    $"{!string.IsNullOrEmpty(choice.matched_stop)}, " +
                    $"raw response length: {rawResponse?.Length ?? 0}.");
                throw new Exception("NovelAI returned an empty response.");
            }
        }

        string[] localStopSequences = stopSequences
            .Concat(new[] { "USER:", "ASSISTANT:", "SYSTEM:" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        output = ApplyLocalStops(output, localStopSequences);
        output = StripLeadingSpeakerLabel(
            output,
            genParams,
            out bool rejectedNonActiveSpeaker);
        output = StripLeadingEmptyActionSeparators(
            output,
            genParams.isDialog);
        output = StripLeadingDashSeparator(output, genParams.isDialog);
        output = EncloseInitialNarrationPrefix(
            output,
            IsFirstMeetingDialog(genParams));
        output = RestoreMissingLeadingActionMarker(output, genParams.isDialog);
        output = JoinLinesAfterLeadingAction(output);

        if (string.IsNullOrWhiteSpace(output) && !retried)
        {
            Plugin.Log.LogWarning(
                rejectedNonActiveSpeaker
                    ? "NovelAI answered as a non-active character; " +
                      "retrying once as the active NPC."
                    : "NovelAI output became empty after removing role " +
                      "boundaries; retrying once.");
            double retryDelay = Math.Max(
                0.0,
                Plugin.RequestDelaySeconds.Value);
            if (retryDelay > 0.0)
                await Task.Delay(TimeSpan.FromSeconds(retryDelay));

            AddRetryCue(BuildBoundaryRetryCue(genParams));
            (choice, rawResponse) = await SendSelected();
            output = GetChoiceText(choice);
            output = ApplyLocalStops(output, localStopSequences);
            output = StripLeadingSpeakerLabel(
                output,
                genParams,
                out _);
            output = StripLeadingEmptyActionSeparators(
                output,
                genParams.isDialog);
            output = StripLeadingDashSeparator(output, genParams.isDialog);
            output = EncloseInitialNarrationPrefix(
                output,
                IsFirstMeetingDialog(genParams));
            output = RestoreMissingLeadingActionMarker(
                output,
                genParams.isDialog);
            output = JoinLinesAfterLeadingAction(output);
        }

        if (string.IsNullOrWhiteSpace(output))
            throw new Exception(
                "NovelAI returned no usable text after one retry.");

        // KoboldClient removes this leading space when the source prompt
        // ended with a prefill space.
        if (hasAssistantPrefill && !output.StartsWith(" "))
            output = " " + output;

        return new APIModelHandler.ChatCompletionsReponse
        {
            provider = "NovelAI",
            choices = new List<APIModelHandler.Choice>
            {
                new()
                {
                    message = new APIModelHandler.Message(
                        APIModelHandler.APIDialogElementType.assistant,
                        output)
                }
            }
        };
    }

    private static string[] BuildServerStopSequences(string[] stopSequences)
    {
        // Silverpine can provide more than twenty character-specific stops.
        // Keep the server request bounded; all stops are still enforced
        // locally after generation.
        return stopSequences
            .Where(sequence => sequence.Length <= 256)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }

    private static async Task<(Choice choice, string rawResponse)>
        SendWithStopFallback(CompletionRequest payload)
    {
        try
        {
            return await SendCompletion(payload);
        }
        catch (NovelAIRequestException exception)
            when (exception.ResponseCode == 400 && payload.stop.Length > 0)
        {
            Plugin.Log.LogWarning(
                "NovelAI rejected server-side stop sequences; retrying " +
                "this request once with local stops only.");
            payload.stop = Array.Empty<string>();
            return await SendCompletion(payload);
        }
    }

    private static async Task EnsureConfiguredModelAvailable()
    {
        string model = Plugin.Model.Value.Trim();
        if (ValidatedModels.Contains(model))
            return;
        if (LastValidationFailureModel == model &&
            DateTime.UtcNow - LastValidationFailureUtc < TimeSpan.FromMinutes(10))
        {
            return;
        }

        using UnityWebRequest request = UnityWebRequest.Get(BaseUrl + "/models");
        request.timeout = 30;
        SetStandardHeaders(request);

        UnityWebRequestAsyncOperation operation = request.SendWebRequest();
        while (!operation.isDone)
            await Task.Yield();

        if (request.result != UnityWebRequest.Result.Success)
        {
            // A temporary model-list outage should not block a generation
            // endpoint that may still be healthy.
            if (request.responseCode >= 500 || request.responseCode == 0)
            {
                Plugin.Log.LogWarning(
                    "Could not validate the configured NovelAI model; " +
                    "continuing with the generation request.");
                LastValidationFailureModel = model;
                LastValidationFailureUtc = DateTime.UtcNow;
                return;
            }
            throw CreateException(request);
        }

        ModelsResponse response = (ModelsResponse)
            StringSerializationAPI.Deserialize(
                typeof(ModelsResponse),
                request.downloadHandler.text);
        if (response?.data == null ||
            !response.data.Any(item => item.id == model))
        {
            throw new Exception(
                $"NovelAI model '{model}' is not available for this account.");
        }

        ValidatedModels.Add(model);
        LastValidationFailureModel = "";
        LastValidationFailureUtc = DateTime.MinValue;
    }

    private static async Task<(Choice choice, string rawResponse)>
        SendCompletion(CompletionRequest payload)
    {
        return await SendGenerationRequest(
            "/completions",
            JsonUtility.ToJson(payload));
    }

    private static async Task<(Choice choice, string rawResponse)>
        SendChat(ChatRequest payload)
    {
        // Use the same serializer Silverpine uses for its native OpenRouter
        // chat requests. Unity's JsonUtility is reliable for the flat text
        // completion payload, but the chat payload contains a nested message
        // list and must preserve every role/content object.
        string json = StringSerializationAPI.SerializeCompact(
            typeof(ChatRequest),
            payload);
        int serializedRoleCount = Regex.Matches(
            json,
            "\\\"role\\\"\\s*:").Count;
        if (serializedRoleCount != payload.messages.Count)
        {
            throw new Exception(
                "NovelAI chat payload serialization failed: expected " +
                $"{payload.messages.Count} messages but serialized " +
                $"{serializedRoleCount} roles.");
        }
        using var request = new UnityWebRequest(
            BaseUrl + "/chat/completions",
            UnityWebRequest.kHttpVerbPOST);
        request.timeout = 60;
        request.uploadHandler = new UploadHandlerRaw(
            Encoding.UTF8.GetBytes(json));
        request.downloadHandler = new DownloadHandlerBuffer();
        SetStandardHeaders(request, "text/event-stream");

        UnityWebRequestAsyncOperation operation = request.SendWebRequest();
        while (!operation.isDone)
            await Task.Yield();

        if (request.result != UnityWebRequest.Result.Success)
            throw CreateException(request);

        string rawResponse = request.downloadHandler.text;
        return (ParseChatEventStream(rawResponse), rawResponse);
    }

    private static async Task<(Choice choice, string rawResponse)>
        SendGenerationRequest(string path, string json)
    {
        using var request = new UnityWebRequest(
            BaseUrl + path,
            UnityWebRequest.kHttpVerbPOST);
        request.timeout = 60;
        request.uploadHandler = new UploadHandlerRaw(
            Encoding.UTF8.GetBytes(json));
        request.downloadHandler = new DownloadHandlerBuffer();
        SetStandardHeaders(request);

        UnityWebRequestAsyncOperation operation = request.SendWebRequest();
        while (!operation.isDone)
            await Task.Yield();

        if (request.result != UnityWebRequest.Result.Success)
            throw CreateException(request);

        string rawResponse = request.downloadHandler.text;
        Response response = (Response)StringSerializationAPI.Deserialize(
            typeof(Response),
            rawResponse);
        if (response?.choices == null || response.choices.Count == 0)
            throw new Exception("NovelAI returned no completion choices.");

        return (response.choices[0], rawResponse);
    }

    private static bool IsChatCompatibilityFailure(long responseCode)
    {
        return responseCode == 400 || responseCode == 404 ||
               responseCode == 405 || responseCode == 422;
    }

    private static string GetChoiceText(Choice choice)
    {
        if (!string.IsNullOrEmpty(choice.text))
            return choice.text;
        if (!string.IsNullOrEmpty(choice.message?.content))
            return choice.message.content;
        return choice.delta?.content;
    }

    private static Choice ParseChatEventStream(string rawResponse)
    {
        var combinedText = new StringBuilder();
        var combinedChoice = new Choice();
        int eventCount = 0;

        foreach (string rawLine in Regex.Split(rawResponse ?? "", "\\r?\\n"))
        {
            if (!rawLine.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            string data = rawLine.Substring("data:".Length).Trim();
            if (data.Length == 0 || data == "[DONE]")
                continue;

            Response response;
            try
            {
                response = (Response)StringSerializationAPI.Deserialize(
                    typeof(Response),
                    data);
            }
            catch (Exception exception)
            {
                Plugin.Log.LogWarning(
                    "NovelAI returned an unreadable chat stream event of " +
                    $"length {data.Length}: {exception.Message}");
                continue;
            }

            if (response?.choices == null || response.choices.Count == 0)
                continue;

            eventCount++;
            Choice chunk = response.choices[0];
            string chunkText = GetChoiceText(chunk);
            if (!string.IsNullOrEmpty(chunkText))
                combinedText.Append(chunkText);
            if (!string.IsNullOrEmpty(chunk.finish_reason))
                combinedChoice.finish_reason = chunk.finish_reason;
            if (!string.IsNullOrEmpty(chunk.matched_stop))
                combinedChoice.matched_stop = chunk.matched_stop;
        }

        if (eventCount == 0)
            throw new Exception(
                "NovelAI returned no readable chat stream events.");

        string streamedText = combinedText.ToString();
        string withoutTransportPadding = streamedText.TrimStart('\r', '\n');
        if (withoutTransportPadding.Length != streamedText.Length)
        {
            Plugin.Log.LogDebug(
                "Removed leading SSE chat line breaks before applying " +
                "Silverpine's newline stop boundary.");
        }
        combinedChoice.text = withoutTransportPadding;
        Plugin.Log.LogDebug(
            $"NovelAI chat stream contained {eventCount} events and " +
            $"{combinedChoice.text.Length} text characters.");
        return combinedChoice;
    }

    private static string DescribeMessageLayout(List<Message> messages)
    {
        int totalCharacters = messages.Sum(
            message => message.content?.Length ?? 0);
        string sequence = string.Join(
            ">",
            messages.Take(32).Select(message =>
                $"{message.role}({message.content?.Length ?? 0})"));
        if (messages.Count > 32)
            sequence += $">...(+{messages.Count - 32})";
        return $"{messages.Count} messages, {totalCharacters} characters " +
               $"[{sequence}]";
    }

    private static string GetSafeErrorDetail(
        NovelAIRequestException exception)
    {
        Match match = Regex.Match(
            exception.Message ?? "",
            @"""message""\s*:\s*""(?<value>(?:\\.|[^""\\])*)""");
        if (!match.Success)
            return "no structured server error message";

        string detail = match.Groups["value"].Value
            .Replace("\\\"", "\"")
            .Replace("\\\\", "\\");
        detail = new string(detail
            .Where(character => !char.IsControl(character))
            .Take(400)
            .ToArray());
        return detail.Length == 0
            ? "empty structured server error message"
            : detail;
    }

    private static void LogRouteOnce(string model, bool useChatEndpoint)
    {
        string route = useChatEndpoint ? "chat" : "text completions";
        if (!LoggedRoutes.Add(model + "|" + route))
            return;

        Plugin.Log.LogInfo(
            $"NovelAI hybrid routing for '{model}' is using {route}.");
    }

    private static void SetStandardHeaders(
        UnityWebRequest request,
        string accept = "application/json")
    {
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Accept", accept);
        request.SetRequestHeader(
            "Authorization",
            "Bearer " + Plugin.GetNovelAIApiKey());
        request.SetRequestHeader(
            "x-correlation-id",
            Guid.NewGuid().ToString("N").Substring(0, 6));
    }

    private static string ApplyLocalStops(
        string output,
        string[] localStopSequences)
    {
        output = StripLeadingAssistantMarker(output ?? "");
        var firstStop = localStopSequences
            .Select(sequence => new
            {
                Sequence = sequence,
                Index = output.IndexOf(
                    sequence,
                    StringComparison.OrdinalIgnoreCase)
            })
            .Where(match => match.Index >= 0)
            .OrderBy(match => match.Index)
            .FirstOrDefault();
        if (firstStop != null)
        {
            if (firstStop.Index == 0 && output.Length > 0)
            {
                Plugin.Log.LogWarning(
                    "NovelAI output began with a Silverpine " +
                    $"{DescribeStopBoundary(firstStop.Sequence)} stop " +
                    "boundary.");
            }
            output = output.Substring(0, firstStop.Index);
        }
        return output;
    }

    private static string DescribeStopBoundary(string sequence)
    {
        if (sequence == "\n" || sequence == "\r\n")
            return "newline";
        if (sequence == NeuralNPC.USER_TAG)
            return "user-role";
        if (sequence == NeuralNPC.ASSISTANT_TAG)
            return "assistant-role";
        if (sequence.Equals("SYSTEM:", StringComparison.OrdinalIgnoreCase))
            return "system-role";
        if (sequence.EndsWith(":"))
            return "speaker-label";
        return $"length-{sequence.Length}";
    }

    private static string StripLeadingAssistantMarker(string output)
    {
        string trimmedStart = output.TrimStart();
        const string marker = "ASSISTANT:";
        if (!trimmedStart.StartsWith(
                marker,
                StringComparison.OrdinalIgnoreCase))
            return output;

        return trimmedStart.Substring(marker.Length).TrimStart();
    }

    private static string StripLeadingSpeakerLabel(
        string output,
        GenParams genParams,
        out bool rejectedNonActiveSpeaker)
    {
        rejectedNonActiveSpeaker = false;
        if (!genParams.isDialog || string.IsNullOrWhiteSpace(output))
            return output;

        Match match = Regex.Match(
            output,
            @"^[ \t]*\*?([^*:\r\n]{1,80}):\*?[ \t]*");
        if (!match.Success)
            return output;

        string label = match.Groups[1].Value.Trim();
        string activeSpeaker = GetActiveDialogSpeakerName();
        if (label.Length == 0 || activeSpeaker.Length == 0)
            return output;

        if (label.Equals(
                activeSpeaker,
                StringComparison.OrdinalIgnoreCase))
        {
            Plugin.Log.LogWarning(
                "Removed the active NPC's model-generated speaker label " +
                "before applying NovelAI dialogue formatting repairs.");
            return output.Substring(match.Length).TrimStart();
        }

        string prompt = genParams.prompt ?? "";
        bool isKnownNpc = NeuralNPC.neuralNPCs != null &&
            NeuralNPC.neuralNPCs.Values.Any(npc =>
                npc != null && npc.GetFinalName().Equals(
                    label,
                    StringComparison.OrdinalIgnoreCase));
        bool appearedAsSpeaker = prompt.IndexOf(
            label + ":",
            StringComparison.OrdinalIgnoreCase) >= 0;
        if (!isKnownNpc && !appearedAsSpeaker)
            return output;

        rejectedNonActiveSpeaker = true;
        Plugin.Log.LogWarning(
            "Rejected NovelAI dialogue that began with a non-active " +
            "character's speaker label.");
        return "";
    }

    private static string BuildBoundaryRetryCue(GenParams genParams)
    {
        string activeSpeaker = GetActiveDialogSpeakerName();
        if (genParams.isDialog && activeSpeaker.Length > 0)
        {
            return "Respond only as " + activeSpeaker + ". Do not write " +
                "dialogue, actions, or thoughts for any other character. " +
                "Do not begin with a character name, USER, ASSISTANT, or " +
                "SYSTEM label.";
        }

        return "Provide only the requested answer now. Do not include a " +
            "USER, ASSISTANT, or SYSTEM label.";
    }

    private static string GetActiveDialogSpeakerName()
    {
        return NeuralNPC.currentActiveDialogNeuralNPC?
            .GetFinalName()?.Trim() ?? "";
    }

    private static string JoinLinesAfterLeadingAction(string output)
    {
        if (!output.TrimStart().StartsWith("*"))
            return output;

        // NeuralNPC.Generate only retains the first output line. Xialong often
        // places dialogue on the line after an opening *action*, so preserve it
        // by making that action/dialogue response a single Silverpine line.
        return output
            .Replace("\r\n", " ")
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    private static string RestoreMissingLeadingActionMarker(
        string output,
        bool isDialog)
    {
        if (!isDialog || string.IsNullOrWhiteSpace(output))
            return output;

        int markerCount = output.Count(character => character == '*');
        int firstMarker = output.IndexOf('*');
        if (markerCount % 2 == 0 || firstMarker <= 0)
            return output;

        string beforeFirstMarker = output.Substring(0, firstMarker);
        if (string.IsNullOrWhiteSpace(beforeFirstMarker))
            return output;

        Plugin.Log.LogWarning(
            "NovelAI dialogue had an unmatched closing action marker; " +
            "restored the missing leading asterisk.");

        int contentStart = 0;
        while (contentStart < output.Length &&
               char.IsWhiteSpace(output[contentStart]))
        {
            contentStart++;
        }
        return output.Insert(contentStart, "*");
    }

    private static string StripLeadingDashSeparator(
        string output,
        bool isDialog)
    {
        if (!isDialog || string.IsNullOrEmpty(output))
            return output;

        int index = 0;
        while (index < output.Length && char.IsWhiteSpace(output[index]))
            index++;

        int dashCount = 0;
        int scan = index;
        while (scan < output.Length)
        {
            char character = output[scan];
            if (character == '-' || character == '\u2013' ||
                character == '\u2014')
            {
                dashCount++;
                scan++;
                continue;
            }
            if (char.IsWhiteSpace(character))
            {
                scan++;
                continue;
            }
            break;
        }

        if (dashCount < 1)
            return output;

        Plugin.Log.LogWarning(
            "Removed a leading dash separator from NovelAI dialogue.");
        return output.Substring(scan).TrimStart();
    }

    private static string StripLeadingEmptyActionSeparators(
        string output,
        bool isDialog)
    {
        if (!isDialog || string.IsNullOrEmpty(output))
            return output;

        string cleaned = Regex.Replace(
            output,
            @"(?m)(^|\n)([^\r\n:]{1,80}:(?:\*)?[ \t]*)?(?:\*\*(?:[ \t]+|$))+",
            "$1$2");
        if (cleaned != output)
        {
            Plugin.Log.LogWarning(
                "Removed empty leading action markers from NovelAI dialogue.");
        }
        return cleaned;
    }

    private static string EncloseInitialNarrationPrefix(
        string output,
        bool isInitialUnpromptedDialog)
    {
        if (!isInitialUnpromptedDialog || string.IsNullOrWhiteSpace(output))
            return output;

        int firstMarker = output.IndexOf('*');
        int markerCount = output.Count(character => character == '*');
        if (firstMarker <= 0 || markerCount == 0 || markerCount % 2 != 0)
            return output;

        string prefix = output.Substring(0, firstMarker);
        string narration = prefix.Trim();
        if (narration.Length == 0)
            return output;

        Plugin.Log.LogWarning(
            "NovelAI's initial NPC introduction began with unmarked " +
            "narration; enclosed the leading segment in asterisks.");

        string leadingWhitespace = prefix.Substring(
            0,
            prefix.Length - prefix.TrimStart().Length);
        return leadingWhitespace + "*" + narration + "* " +
               output.Substring(firstMarker).TrimStart();
    }

    private static bool IsFirstMeetingDialog(GenParams genParams)
    {
        return genParams.isDialog &&
               (genParams.prompt ?? "").IndexOf(
                   "first time meeting",
                   StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsConversationBroadcast(GenParams genParams)
    {
        string prompt = genParams.prompt ?? "";
        return prompt.IndexOf(
                   "Summarize this conversation between ",
                   StringComparison.OrdinalIgnoreCase) >= 0 &&
               prompt.IndexOf(
                   "had a conversation about ",
                   StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int GetPrefillLength(List<Message> messages)
    {
        if (messages.Count == 0 ||
            messages[messages.Count - 1].role != "assistant")
            return 0;
        return messages[messages.Count - 1].content?.Length ?? 0;
    }

    private static List<Message> BuildChatMessages(
        List<Message> messages,
        bool isDialog,
        string grammar)
    {
        var chatMessages = new List<Message>
        {
            new()
            {
                role = "system",
                content = BuildInstructionText(isDialog, grammar)
            }
        };

        // Copy rather than reuse the converted messages. Empty-response retry
        // cues may extend the chat request and must not alter the completion
        // fallback's source data.
        chatMessages.AddRange(messages.Select(message => new Message
        {
            role = message.role,
            content = message.content
        }));

        if (isDialog)
        {
            // Silverpine may finish a multi-dialog prompt with an explicit
            // system turn marker such as "(Gareth's turn.)". Put the named
            // ownership rule on the last system message so it remains next
            // to that marker instead of being diluted by character history.
            int lastSystemIndex = chatMessages.FindLastIndex(message =>
                message.role == "system");
            if (lastSystemIndex > 0)
            {
                chatMessages[lastSystemIndex].content +=
                    "\n\n" + BuildActiveSpeakerInstruction();
            }
        }
        return chatMessages;
    }

    private static string BuildInstructionText(bool isDialog, string grammar)
    {
        var instructions = new StringBuilder(
            isDialog ? CompatibilityInstructions : UtilityInstructions);
        if (isDialog)
        {
            instructions.Append("\n\n");
            instructions.Append(BuildActiveSpeakerInstruction());
        }
        if (!isDialog && !string.IsNullOrWhiteSpace(grammar))
        {
            instructions.Append(
                "\nThe response must strictly match this GBNF grammar; " +
                "do not repeat the grammar:\n");
            instructions.Append(grammar);
        }
        return instructions.ToString();
    }

    private static string BuildActiveSpeakerInstruction()
    {
        string activeSpeaker = GetActiveDialogSpeakerName();
        if (activeSpeaker.Length == 0)
        {
            return "Generate exactly one character's turn. Only the active " +
                "NPC may speak, act, think, feel, decide, or react. Do not " +
                "control the player or any other character.";
        }

        return "ACTIVE TURN OWNERSHIP: The one and only character you may " +
            "write or control in this response is " + activeSpeaker + ". " +
            "Every spoken word must belong to " + activeSpeaker + ", and " +
            "every action, reaction, decision, feeling, and thought you " +
            "invent must be " + activeSpeaker + "'s. The player and all " +
            "other named characters are context only: do not make them " +
            "speak, move, react, decide, feel, or think. Do not continue " +
            "their side of the scene. Return only " + activeSpeaker + "'s " +
            "single turn, without a speaker label.";
    }

    private static string BuildCompletionPrompt(
        List<Message> messages,
        bool isDialog,
        string grammar)
    {
        var prompt = new StringBuilder();
        prompt.Append("SYSTEM:\n");
        prompt.Append(BuildInstructionText(isDialog, grammar));
        prompt.Append("\n\n");

        for (int index = 0; index < messages.Count; index++)
        {
            Message message = messages[index];
            prompt.Append(message.role.ToUpperInvariant());
            prompt.Append(":\n");
            prompt.Append(message.content);

            // A final assistant message is Silverpine's completion prefill
            // (for example, an overheard-conversation summary ending in
            // "had a conversation about "). Do not terminate that unfinished
            // turn with a blank line or Xialong may stop without answering.
            bool isFinalAssistantPrefill =
                index == messages.Count - 1 &&
                message.role == "assistant";
            if (!isFinalAssistantPrefill)
                prompt.Append("\n\n");
        }

        if (messages.Count == 0 ||
            messages[messages.Count - 1].role != "assistant")
        {
            prompt.Append("ASSISTANT:\n");
        }
        return prompt.ToString();
    }

    private static List<Message> ConvertMessages(string prompt)
    {
        var converted = new List<Message>();
        var original = (List<APIModelHandler.Message>)Traverse
            .Create(typeof(APIModelHandler))
            .Method("ConvertRawPromptToApiDialogElements", prompt)
            .GetValue();

        foreach (APIModelHandler.Message message in original)
        {
            if (message == null || message.content == null)
                continue;

            string content = SanitizeContent(message.content);
            if (message.role.ToString() == "assistant")
                content = StripHistoricalDashSeparators(content);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            converted.Add(new Message
            {
                role = message.role.ToString(),
                content = content
            });
        }

        if (!converted.Any(message => message.role == "user"))
        {
            converted.Add(new Message
            {
                role = "user",
                content = string.IsNullOrEmpty(prompt)
                    ? "Continue."
                    : SanitizeContent(prompt)
            });
        }
        return converted;
    }

    private static string SanitizeContent(string content)
    {
        if (string.IsNullOrEmpty(content))
            return "";

        var clean = new StringBuilder(content.Length);
        foreach (char character in content)
        {
            if (character == '\r' ||
                character == '\n' ||
                character == '\t' ||
                !char.IsControl(character))
            {
                clean.Append(character);
            }
        }
        return clean.ToString();
    }

    private static string StripHistoricalDashSeparators(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        // Remove empty action spans already stored after a speaker label so
        // Xialong cannot imitate and lengthen them in later responses.
        content = Regex.Replace(
            content,
            @"(?m)(^|\n)([^\r\n:]{1,80}:(?:\*)?[ \t]*)?(?:\*\*(?:[ \t]+|$))+",
            "$1$2");

        // Multi-dialog responses are stored with the speaker label. Remove
        // separator runs already present after labels so the model cannot
        // imitate and lengthen them on every subsequent turn.
        content = Regex.Replace(
            content,
            @"(?m)(^|\n)([^\r\n:]{1,80}:\s*)(?:[-\u2013\u2014]\s*){1,}",
            "$1$2");
        return Regex.Replace(
            content,
            @"(?m)^[ \t]*(?:[-\u2013\u2014][ \t]*){1,}",
            "");
    }

    private static NovelAIRequestException CreateException(
        UnityWebRequest request)
    {
        string detail = request.downloadHandler?.text;
        string message = request.responseCode switch
        {
            401 => "The NovelAI access token is invalid or expired.",
            402 => "The NovelAI account cannot make this generation.",
            429 => "NovelAI rate-limited the request.",
            >= 500 => "NovelAI is temporarily unavailable.",
            _ => $"NovelAI request failed ({request.responseCode}): " +
                 (string.IsNullOrWhiteSpace(detail) ? request.error : detail)
        };
        return new NovelAIRequestException(request.responseCode, message);
    }
}
