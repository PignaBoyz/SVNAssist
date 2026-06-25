using System.Net;
using System.Net.Http.Headers;
using RichardSzalay.MockHttp;
using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>
/// Test per <see cref="AiService"/> con mock HTTP.
/// Non servono chiamate reali a OpenAI — il comportamento viene simulato
/// con <see cref="MockHttpMessageHandler"/> che intercetta le richieste HTTP
/// e restituisce risposte predefinite.
/// </summary>
/// <remarks>
/// Perché mockare HttpClient e non l'interfaccia IAiService?
/// Perché qui vogliamo testare la logica interna di AiService:
/// - Costruzione corretta del body JSON
/// - Parsing della risposta Chat Completions
/// - Gestione degli errori HTTP
/// I test dei consumatori (CommitDialog, ecc.) mockeranno IAiService.
/// </remarks>
public class AiServiceTests
{
    private const string TestEndpoint = "https://api.test.com/v1/chat/completions";
    private const string TestModel = "test-model";
    private const string TestApiKey = "test-key";

    /// <summary>
    /// Genera una risposta JSON nel formato OpenAI Chat Completions.
    /// </summary>
    private static string MakeChatResponse(string content) =>
        $$"""
        {
          "id": "chatcmpl-test",
          "object": "chat.completion",
          "choices": [
            {
              "index": 0,
              "message": {
                "role": "assistant",
                "content": "{{content}}"
              },
              "finish_reason": "stop"
            }
          ]
        }
        """;

    [Fact]
    public async Task GenerateCommitMessageAsync_RispostaValida_RestituisceMessaggio()
    {
        // Arrange
        var expectedMessage = "fix: corretto il parsing del file XML";
        var mockHttp = new MockHttpMessageHandler();

        mockHttp
            .When(TestEndpoint)
            .Respond("application/json", MakeChatResponse(expectedMessage));

        var httpClient = mockHttp.ToHttpClient();
        var sut = new AiService(httpClient, TestEndpoint, TestModel, TestApiKey);

        // Act
        var result = await sut.GenerateCommitMessageAsync(
            "--- a/file.cs\n+++ b/file.cs\n@@ -1 +1 @@\n-old\n+new",
            "italiano",
            CancellationToken.None);

        // Assert
        Assert.Equal(expectedMessage, result);
    }

    [Fact]
    public async Task GenerateCommitMessageAsync_LinguaAuto_NonIncludeLinguaNelPrompt()
    {
        // Arrange
        var mockHttp = new MockHttpMessageHandler();

        // Cattura la richiesta per verificare il contenuto
        string? capturedBody = null;
        mockHttp
            .When(TestEndpoint)
            .Respond(async req =>
            {
                capturedBody = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        MakeChatResponse("test message"),
                        System.Text.Encoding.UTF8,
                        "application/json"),
                };
            });

        var httpClient = mockHttp.ToHttpClient();
        var sut = new AiService(httpClient, TestEndpoint, TestModel, TestApiKey);

        // Act
        await sut.GenerateCommitMessageAsync("diff content", "auto", CancellationToken.None);

        // Assert — con "auto" il prompt non deve contenere "Lingua:"
        Assert.NotNull(capturedBody);
        Assert.DoesNotContain("Lingua:", capturedBody);
    }

    [Fact]
    public async Task GenerateCommitMessageAsync_LinguaSpecifica_IncludeLinguaNelPrompt()
    {
        // Arrange
        var mockHttp = new MockHttpMessageHandler();

        string? capturedBody = null;
        mockHttp
            .When(TestEndpoint)
            .Respond(async req =>
            {
                capturedBody = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        MakeChatResponse("fixed XML parsing"),
                        System.Text.Encoding.UTF8,
                        "application/json"),
                };
            });

        var httpClient = mockHttp.ToHttpClient();
        var sut = new AiService(httpClient, TestEndpoint, TestModel, TestApiKey);

        // Act
        await sut.GenerateCommitMessageAsync("diff content", "english", CancellationToken.None);

        // Assert — con "english" il prompt deve contenere "Lingua: english."
        Assert.NotNull(capturedBody);
        Assert.Contains("Lingua: english.", capturedBody);
    }

    [Fact]
    public async Task ImproveCommitMessageAsync_RispostaValida_RestituisceMessaggioMigliorato()
    {
        // Arrange
        var improvedMessage = "refactor: migliorata la gestione degli errori nel parser XML";
        var mockHttp = new MockHttpMessageHandler();

        mockHttp
            .When(TestEndpoint)
            .Respond("application/json", MakeChatResponse(improvedMessage));

        var httpClient = mockHttp.ToHttpClient();
        var sut = new AiService(httpClient, TestEndpoint, TestModel, TestApiKey);

        // Act
        var result = await sut.ImproveCommitMessageAsync(
            "fix xml",
            "--- a/parser.cs\n+++ b/parser.cs\n@@ -10 +10 @@\n-catch\n+catch (XmlException ex)",
            "italiano",
            CancellationToken.None);

        // Assert
        Assert.Equal(improvedMessage, result);
    }

    [Fact]
    public async Task ImproveCommitMessageAsync_IncludeDraftEDiffNelBody()
    {
        // Arrange
        var mockHttp = new MockHttpMessageHandler();

        string? capturedBody = null;
        mockHttp
            .When(TestEndpoint)
            .Respond(async req =>
            {
                capturedBody = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        MakeChatResponse("improved message"),
                        System.Text.Encoding.UTF8,
                        "application/json"),
                };
            });

        var httpClient = mockHttp.ToHttpClient();
        var sut = new AiService(httpClient, TestEndpoint, TestModel, TestApiKey);

        // Act
        await sut.ImproveCommitMessageAsync(
            "bozza originale",
            "diff di riferimento",
            "italiano",
            CancellationToken.None);

        // Assert — il body deve contenere sia il draft che il diff
        Assert.NotNull(capturedBody);
        Assert.Contains("bozza originale", capturedBody);
        Assert.Contains("diff di riferimento", capturedBody);
    }

    [Fact]
    public async Task GenerateCommitMessageAsync_InviaAuthorizationHeaderPerRichiesta()
    {
        // Arrange — il client NON ha DefaultRequestHeaders.Authorization impostato:
        // l'header deve arrivare comunque perché AiService lo applica per-richiesta.
        var mockHttp = new MockHttpMessageHandler();

        AuthenticationHeaderValue? capturedAuth = null;
        mockHttp
            .When(TestEndpoint)
            .Respond(req =>
            {
                capturedAuth = req.Headers.Authorization;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        MakeChatResponse("ok"),
                        System.Text.Encoding.UTF8,
                        "application/json"),
                };
            });

        var httpClient = mockHttp.ToHttpClient();
        var sut = new AiService(httpClient, TestEndpoint, TestModel, TestApiKey);

        // Act
        await sut.GenerateCommitMessageAsync("diff", "auto", CancellationToken.None);

        // Assert
        Assert.NotNull(capturedAuth);
        Assert.Equal("Bearer", capturedAuth!.Scheme);
        Assert.Equal(TestApiKey, capturedAuth.Parameter);
    }

    [Fact]
    public async Task GenerateCommitMessageAsync_Errore500_LanciaHttpRequestException()
    {
        // Arrange
        var mockHttp = new MockHttpMessageHandler();

        mockHttp
            .When(TestEndpoint)
            .Respond(HttpStatusCode.InternalServerError);

        var httpClient = mockHttp.ToHttpClient();
        var sut = new AiService(httpClient, TestEndpoint, TestModel, TestApiKey);

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.GenerateCommitMessageAsync("diff", "auto", CancellationToken.None));
    }

    [Fact]
    public async Task GenerateCommitMessageAsync_RispostaSenzaChoices_LanciaInvalidOperation()
    {
        // Arrange
        var mockHttp = new MockHttpMessageHandler();

        mockHttp
            .When(TestEndpoint)
            .Respond("application/json", """{ "choices": [] }""");

        var httpClient = mockHttp.ToHttpClient();
        var sut = new AiService(httpClient, TestEndpoint, TestModel, TestApiKey);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.GenerateCommitMessageAsync("diff", "auto", CancellationToken.None));
    }

    [Fact]
    public async Task GenerateCommitMessageAsync_InviaModelCorretto()
    {
        // Arrange
        var mockHttp = new MockHttpMessageHandler();

        string? capturedBody = null;
        mockHttp
            .When(TestEndpoint)
            .Respond(async req =>
            {
                capturedBody = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        MakeChatResponse("test"),
                        System.Text.Encoding.UTF8,
                        "application/json"),
                };
            });

        var httpClient = mockHttp.ToHttpClient();
        var sut = new AiService(httpClient, TestEndpoint, TestModel, TestApiKey);

        // Act
        await sut.GenerateCommitMessageAsync("diff", "auto", CancellationToken.None);

        // Assert — il body deve contenere il nome del modello
        Assert.NotNull(capturedBody);
        Assert.Contains($"\"{TestModel}\"", capturedBody);
    }
}
