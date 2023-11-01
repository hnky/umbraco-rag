using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace UmbracoRAG
{
    public class NotificationHandlersComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.AddNotificationAsyncHandler<ContentSavingNotification, CustomContentSavingNotificationHandler>();
        }
    }

    public class CustomContentSavingNotificationHandler : INotificationAsyncHandler<ContentSavingNotification>
    {
        private readonly IOptions<AzureSearchConfig> _azureSearchConfig;

        public CustomContentSavingNotificationHandler(IOptions<AzureSearchConfig> app) {
            _azureSearchConfig = app;
        }

        private static async Task<IReadOnlyList<float>> GenerateEmbeddings(string text, OpenAIClient openAIClient)
        {
            var response = await openAIClient.GetEmbeddingsAsync("text-embedding-ada-002", new EmbeddingsOptions(text));
            return response.Value.Data[0].Embedding;
        }

        public async Task HandleAsync(ContentSavingNotification notification, CancellationToken cancellationToken)
        {

            // Initialize Azure OpenAI Client
            Uri oaiEndpoint = new (_azureSearchConfig.Value.oaiEndpoint);
            string oaiKey = _azureSearchConfig.Value.oaiKey;
            AzureKeyCredential credentials = new (oaiKey);
            OpenAIClient openAIClient = new (oaiEndpoint, credentials);

            // Initialize Azure Cognitive Search clients  
            var searchCredential = new AzureKeyCredential(_azureSearchConfig.Value.apikey);
            var indexClient = new SearchIndexClient(new Uri(_azureSearchConfig.Value.endpoint), searchCredential);
            var searchClient = indexClient.GetSearchClient(_azureSearchConfig.Value.indexname);

            // Create the index if it doesn't exist
            indexClient.CreateOrUpdateIndex(
                new(_azureSearchConfig.Value.indexname)
                {
                    VectorSearch = new()
                    {
                        Profiles = { new VectorSearchProfile("my-vector-profile", "my-hnsw-vector-config") },
                        Algorithms = { new HnswVectorSearchAlgorithmConfiguration("my-hnsw-vector-config") }
                    },
                    Fields =
                    {
                        new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true, IsSortable = true, IsFacetable = true },
                        new SearchableField("title") { IsFilterable = true, IsSortable = true },
                        new SearchableField("content") { IsFilterable = true },
                        new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                        {
                            IsSearchable = true,
                            VectorSearchDimensions = 1536,
                            VectorSearchProfile = "my-vector-profile"
                        }
                    }
                }
            );

            // Create a list of documents to be indexed
            List<SearchDocument> productDocuments = new List<SearchDocument>();

            // Loop through all the saved entities
            foreach (var content in notification.SavedEntities.Where(c => c.ContentType.Alias.InvariantEquals("product")))
            {
                // Create an embedding from the content in the content field
                string contentForEmbedding = content.GetValue<string>("content");
                float[] contentEmbedding = (await GenerateEmbeddings(contentForEmbedding, openAIClient)).ToArray();

                // Create a document for the search index
                var document = new Dictionary<string, object>
                {
                    {"id", content.Id.ToString()},
                    {"title", content.Name},
                    {"content", contentForEmbedding},
                    {"contentVector", contentEmbedding}
                };

                // Add the document to list of documents to be indexed
                productDocuments.Add(new SearchDocument(document));
            }

            // Index the documents if any
            if(productDocuments.Any()){
                await searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Upload(productDocuments));
            }
            
        }

    }
}