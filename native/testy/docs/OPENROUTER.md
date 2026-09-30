# OpenRouter connection

Testy uses OpenRouter through the **Compatible** provider. Models choose one local UI tool action per turn and receive fresh control state and screenshots before choosing again. Saved actions and assertions are independently verified by Testy.

In **Settings**, set:

| Setting | Value |
| --- | --- |
| Service | Compatible |
| Model | `openai/gpt-4.1-mini` |
| Server URL | `https://openrouter.ai/api/v1/chat/completions` |
| Key from an environment variable | `OPENROUTER_API_KEY` |
| Let the AI guide each run | On |
| OpenAI computer-use mode | Off |
| Send screenshots to the AI | On |
| Most AI actions per run | 12 |

Click **Save**. The key belongs in the named Windows environment variable, not in the endpoint, model, test JSON, or provider settings file. Testy reads the process environment first and then the Windows user environment when the process has no value. The distribution contains no key. `examples/provider-openrouter.json` supplies the configuration for CLI use.

For the included test app, click **Sample app**, select **Create a customer**, and click **Run**. **New test from a description**, below the test list, creates new editable tests from natural language. `google/gemini-2.5-flash-lite` is another model configuration exercised by the live verification. See `verification/OPENROUTER-RESULTS.md` in the source workspace, or `OPENROUTER-VERIFICATION.md` in the portable bundle, for actual outcomes and limitations.

OpenRouter connections in this configuration use Testy's local function tools, including when the selected model is a GPT model. This route does not claim to use OpenAI's native Responses computer tool. Native Responses computer use remains a separate provider configuration.

Choose a model and endpoint that support tool calling and structured outputs. Enable screenshots only for models accepting images. A failed, truncated, parallel, or invalid action response is rejected before dispatch. A model's completion message cannot override failed or missing saved assertions. Usage is billed by OpenRouter; every model decision and explanation is a request.

Official references checked during integration:

- [OpenRouter tool calling](https://openrouter.ai/docs/guides/features/tool-calling)
- [Structured outputs](https://openrouter.ai/docs/guides/features/structured-outputs)
- [Model catalogue](https://openrouter.ai/api/v1/models)
