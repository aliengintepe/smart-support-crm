using System.ComponentModel.DataAnnotations;

namespace SmartSupportCRM.Web.Models;

public sealed class CreateTicketViewModel
{
    [Required, EmailAddress, StringLength(254)]
    public string CustomerEmail { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string Subject { get; set; } = string.Empty;

    [Required, StringLength(12000, MinimumLength = 3)]
    public string Content { get; set; } = string.Empty;
}
