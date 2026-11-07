using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.System.Backup;
using Microsoft.OpenApi;
using NzbDrone.Core.Backup;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace NzbDrone.Host
{
    public sealed class BookshelfBackupOpenApiOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            if (context.MethodInfo?.DeclaringType != typeof(BackupController))
            {
                return;
            }

            var passphrase = operation.Parameters?.FirstOrDefault(parameter =>
                parameter.In == ParameterLocation.Header &&
                string.Equals(parameter.Name, BookshelfEncryptedBackupService.PassphraseHeader, StringComparison.OrdinalIgnoreCase));

            if (passphrase != null)
            {
                var parameterIndex = operation.Parameters.IndexOf(passphrase);
                operation.Parameters[parameterIndex] = new OpenApiParameter
                {
                    Name = BookshelfEncryptedBackupService.PassphraseHeader,
                    In = passphrase.In,
                    Description = passphrase.Description,
                    Required = context.MethodInfo.Name == nameof(BackupController.CreateEncryptedBackup) || passphrase.Required,
                    Schema = new OpenApiSchema
                    {
                        Type = passphrase.Schema?.Type,
                        MinLength = BookshelfEncryptedBackupService.MinimumPassphraseLength,
                        MaxLength = BookshelfEncryptedBackupService.MaximumPassphraseBytes,
                        Format = "password"
                    }
                };
            }

            if (context.MethodInfo.Name == nameof(BackupController.CreateEncryptedBackup) && operation.Responses.TryGetValue("200", out var encryptedResponse))
            {
                encryptedResponse.Description = "Encrypted backup archive";
            }

            if (context.MethodInfo.Name != nameof(BackupController.UploadAndRestore) || operation.RequestBody == null)
            {
                return;
            }

            var requestBody = new OpenApiRequestBody
            {
                Description = operation.RequestBody.Description,
                Required = true,
                Content = operation.RequestBody.Content?.ToDictionary(
                    content => content.Key,
                    content => new OpenApiMediaType
                    {
                        Schema = content.Value.Schema == null ? null : new OpenApiSchema
                        {
                            Type = content.Value.Schema.Type,
                            Properties = content.Value.Schema.Properties,
                            Required = new HashSet<string>(content.Value.Schema.Required ?? new HashSet<string>())
                        },
                        Encoding = content.Value.Encoding
                    })
            };

            var formContent = requestBody.Content?
                .FirstOrDefault(content => string.Equals(content.Key, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
                .Value;

            if (formContent?.Schema != null)
            {
                var formSchema = (OpenApiSchema)formContent.Schema;
                var requiredProperties = new HashSet<string>(formSchema.Required ?? new HashSet<string>());
                requiredProperties.Add("restore");
                formSchema.Required = requiredProperties;
                formContent.Schema = formSchema;
            }

            operation.RequestBody = requestBody;
        }
    }
}
