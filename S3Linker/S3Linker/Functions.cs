using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;


using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;

using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;

using Newtonsoft.Json;
using Amazon.S3;
using Amazon.S3.Model;
using System.IO;
using System.Xml.Serialization;
using System.Text;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializerAttribute(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace S3Linker
{
    public class Functions
    {
        // This const is the name of the environment variable that the serverless.template will use to set
        // the name of the DynamoDB table used to store folders.
        const string TABLENAME_ENVIRONMENT_VARIABLE_LOOKUP = "FolderTable";

        public const string FORMAT_STRING_NAME = "Format";
        public const string ID_FOLDER_STRING_NAME = "Id";
        public const string PATH_FOLDER_STRING_NAME = "Path";
        IDynamoDBContext DDBContext { get; set; }

        string bucket = "akb-lambda-bucket";

        /// <summary>
        /// Default constructor that Lambda will invoke.
        /// </summary>
        public Functions()
        {
            // Check to see if a table name was passed in through environment variables and if so 
            // add the table mapping.
            var tableName = System.Environment.GetEnvironmentVariable(TABLENAME_ENVIRONMENT_VARIABLE_LOOKUP);
            if(!string.IsNullOrEmpty(tableName))
            {
                AWSConfigsDynamoDB.Context.TypeMappings[typeof(Folder)] = new Amazon.Util.TypeMapping(typeof(Folder), tableName);
            }

            var config = new DynamoDBContextConfig { Conversion = DynamoDBEntryConversion.V2 };
            this.DDBContext = new DynamoDBContext(new AmazonDynamoDBClient(), config);
        }

        /// <summary>
        /// Constructor used for testing passing in a preconfigured DynamoDB client.
        /// </summary>
        /// <param name="ddbClient"></param>
        /// <param name="tableName"></param>
        public Functions(IAmazonDynamoDB ddbClient, string tableName)
        {
            if (!string.IsNullOrEmpty(tableName))
            {
                AWSConfigsDynamoDB.Context.TypeMappings[typeof(Folder)] = new Amazon.Util.TypeMapping(typeof(Folder), tableName);
            }

            var config = new DynamoDBContextConfig { Conversion = DynamoDBEntryConversion.V2 };
            this.DDBContext = new DynamoDBContext(ddbClient, config);
        }


        /// <summary>
        /// A Lambda function that returns the content of folder identified by Id
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public async Task<APIGatewayProxyResponse> GetFolderAsync(APIGatewayProxyRequest request, ILambdaContext context)
        {
            try
            {
                IEnumerable<Entry> result = await GetFolderContentAsync(request, context);
                var format = request?.PathParameters[FORMAT_STRING_NAME];
                var response = new APIGatewayProxyResponse();
                response.StatusCode = (int)HttpStatusCode.OK;
                switch (format.ToUpper())
                {
                    case "JSON":
                        response.Body = JsonConvert.SerializeObject(result);
                        response.Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } };
                        break;
                    case "HTML":
                        string folderPath = "";
                        try { folderPath = request?.PathParameters[PATH_FOLDER_STRING_NAME]; } catch { }
                        if(!String.IsNullOrEmpty(folderPath))
                        {
                            string baseUrl = getBaseUrl(request);
                            int idx = baseUrl.LastIndexOf('/');
                            folderPath = baseUrl.Substring(0, idx);
                        }
                        response.Body = GetHtml(result,folderPath); 
                        response.Headers = new Dictionary<string, string> { { "Content-Type", "text/html" } };
                        break;
                    case "XML":
                        XmlSerializer serializer = new XmlSerializer(result.GetType());
                        using (StringWriter writer = new StringWriter())
                        {
                            serializer.Serialize(writer, result);
                            response.Body = writer.ToString();
                        }
                        response.Headers = new Dictionary<string, string> { { "Content-Type", "application/xml" } };
                        break;
                    default:
                        throw new Exception("Invalid format");
                }
                
                return response;
            }catch(Exception ex)
            {
                return new APIGatewayProxyResponse{
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Body = ex.Message
                };
            }
        }

        private string GetHtml(IEnumerable<Entry> folderList,string goUpUrl=null)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("<table><tr><th>Size</th><th>Name</th></tr>");
            if (!String.IsNullOrEmpty(goUpUrl))
                builder.AppendLine($"<tr><td>Parent</td><td><a href='{goUpUrl}'>..</a></td></tr>");
            foreach(Entry entry in folderList)
            {
                builder.AppendLine("<tr>");
                builder.Append("<td>");
                builder.Append(entry.IsFolder ? "Folder" : entry.FileSize.ToString());
                builder.Append("</td>");
                builder.Append($"<td><a href='{entry.Url}'>{entry.RelativePath}</a></td>");
                builder.AppendLine("</tr>");
            }
            builder.Append("</table>");
            return builder.ToString();
        }

        public async Task<IEnumerable<Entry>>  GetFolderContentAsync(APIGatewayProxyRequest request, ILambdaContext context)
        {
            //context.Logger.LogLine($"Request: {JsonConvert.SerializeObject(request)}");
            //context.Logger.LogLine($"Context: {JsonConvert.SerializeObject(context)}");
            String baseUrl = getBaseUrl(request);

            var folderId = request?.PathParameters[ID_FOLDER_STRING_NAME];
            var folderPath = "";
            try { folderPath = request?.PathParameters[PATH_FOLDER_STRING_NAME]; } catch { }

            if (string.IsNullOrEmpty(folderId))
            {
                throw new Exception("Missing required parameter folderId");
            }

            var folder = await DDBContext.LoadAsync<Folder>(folderId);

            if (folder == null || folder.ExpirationTime < DateTime.Now)//if expired, we just return not found
            {
                throw new Exception("Not found");
            }

            string s3prefix = folder.Prefix;
            if (folderPath.Length > 0)
            {
                s3prefix += "/" + WebUtility.UrlDecode(folderPath);

            }
            if (s3prefix[s3prefix.Length - 1] != '/')
                s3prefix += "/";
            //context.Logger.LogLine($"S3 prefix: {s3prefix}");
            List<Entry> result = new List<Entry>();
            //list folder content
            using (IAmazonS3 client = new AmazonS3Client())
            {
                // Build your request to list objects in the bucket
                ListObjectsRequest s3request = new ListObjectsRequest
                {
                    BucketName = bucket,
                    Prefix = s3prefix
                };
                List<string> folders = new List<string>();
                List<string> files = new List<string>();
                while (true)
                {
                    ListObjectsResponse s3response = await client.ListObjectsAsync(s3request);
                    if (!s3response.S3Objects.Any())
                        context.Logger.LogLine("No matching folders");

                    foreach (S3Object s3object in s3response.S3Objects)
                    {
                        context.Logger.LogLine($"Key: {s3object.Key}");
                        string relativePath = s3object.Key.Substring(s3prefix.Length);
                        context.Logger.LogLine($"Relative path: {relativePath}");
                        if (relativePath == "") continue; //current folder 


                        int idx = relativePath.LastIndexOf("/");
                        if (idx < 0) //file in current folder
                        {
                            GetPreSignedUrlRequest presignedRequest = new GetPreSignedUrlRequest()
                            {
                                BucketName = bucket,
                                Key = s3object.Key,
                                Expires = folder.ExpirationTime
                            };
                            Entry entry = new Entry
                            {
                                RelativePath = relativePath,
                                Url = client.GetPreSignedURL(presignedRequest),
                                IsFolder = false,
                                FileSize = s3object.Size
                            };
                            result.Add(entry);
                        }
                        else//subfolder
                        {
                            int idx2 = relativePath.IndexOf("/");
                            string folderName = relativePath;
                            if (idx2 > 0) folderName = relativePath.Substring(0, idx2);
                            if (!folders.Contains(folderName))
                            {
                                folders.Add(folderName);
                                string url = baseUrl;
                                url += $"/{WebUtility.UrlEncode(folderName)}";
                                Entry entry = new Entry
                                {
                                    RelativePath = folderName,
                                    Url = url,
                                    IsFolder = true
                                };
                                result.Add(entry);
                            }
                        }
                    }

                    //there might be more items
                    if (s3response.IsTruncated)
                    {
                        s3request.Marker = s3response.NextMarker;
                    }
                    else
                        break;
                }
            }
            //folders first, alphabeticaly
            //then files, alphabeticaly 
            result.Sort((entry1, entry2) => {
                if (entry1.IsFolder == entry2.IsFolder)
                    return entry1.RelativePath.CompareTo(entry2.RelativePath);
                if (entry1.IsFolder)
                    return -1;
                return 1;
            });

            return result;
        }
        private string getBaseUrl(APIGatewayProxyRequest request)
        {
            string result = "https://"; //API gateway always uses HTTPS 
            result += request.Headers["Host"];
            result += "/";
            result += request.RequestContext.Stage;
            result += request.Path;
            //ensure no trailing '/'
            if (result[result.Length - 1] == '/')
                result = result.Substring(0, result.Length - 1);
            return result;
        }

        /// <summary>
        /// A Lambda function that adds a folder.
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public async Task<APIGatewayProxyResponse> AddFolderAsync(APIGatewayProxyRequest request, ILambdaContext context)
        {
            var folder = JsonConvert.DeserializeObject<Folder>(request?.Body);
            folder.Id = Guid.NewGuid().ToString("N");//just 32 digits, no hyphens, no curly brackets
            folder.ExpirationTime = DateTime.Now + TimeSpan.FromDays(3);
            //prefix must not start and end with '/' as welll as spaces
            folder.Prefix = folder.Prefix.Trim(new char[]{' ', '/'});

            context.Logger.LogLine($"Creating folder with id {folder.Id}");
            await DDBContext.SaveAsync<Folder>(folder);

            var response = new APIGatewayProxyResponse
            {
                StatusCode = (int)HttpStatusCode.OK,
                Body = JsonConvert.SerializeObject(folder),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };
            return response;
        }

        /// <summary>
        /// A Lambda function that removes a folder from the DynamoDB table.
        /// </summary>
        /// <param name="request"></param>
        public async Task<APIGatewayProxyResponse> RemoveFolderAsync(APIGatewayProxyRequest request, ILambdaContext context)
        {
            return new APIGatewayProxyResponse
            {
                StatusCode = (int)HttpStatusCode.OK,
                Body = "Removing folder"
            };
            var blogId = request?.PathParameters[ID_FOLDER_STRING_NAME];
            if (string.IsNullOrEmpty(blogId))
                blogId = request?.QueryStringParameters[ID_FOLDER_STRING_NAME];

            if (string.IsNullOrEmpty(blogId))
            {
                return new APIGatewayProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Body = "Missing required parameter blogId"
                };
            }

            context.Logger.LogLine($"Deleting folder with id {blogId}");
            await this.DDBContext.DeleteAsync<Folder>(blogId);

            return new APIGatewayProxyResponse
            {
                StatusCode = (int)HttpStatusCode.OK
            };
        }
    }
}
