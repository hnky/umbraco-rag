using System.ComponentModel.DataAnnotations;

namespace UmbracoRAG.Models.Forms
{
    public class SearchFormViewModel 
    {
        [Required]
        public string UserMessage { get; set; }

    }
}