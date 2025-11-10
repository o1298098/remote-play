
using Newtonsoft.Json;
using RemotePlay.Models.Base;
using System.Net.Http.Headers;

namespace STIot.Utils
{
    public class HttpRequest
    {
        //private readonly HttpClient _client;
        private readonly IHttpClientFactory _factory;
        string _baseUrl;
        private readonly string _clientName;
        public HttpRequest(string baseUrl, IHttpClientFactory factory,
            string clientName = "Default")
        {
            _baseUrl = baseUrl;
            _factory = factory;
            _clientName = clientName;
            //   _client = new HttpClient();
            //  _client.BaseAddress = new Uri(baseUrl);
        }

        public async Task<ResponseModel> Request(string prama, Dictionary<string, string>? headers = null, System.Net.Http.Headers.AuthenticationHeaderValue? auth = null)
        {
            var _client = _factory.CreateClient(_clientName);
            _client.Timeout = TimeSpan.FromMinutes(10);
            _client.BaseAddress = new Uri(_baseUrl);
            ResponseModel responseModel = new ResponseModel();
            try
            {
                if (auth != null) _client.DefaultRequestHeaders.Authorization = auth;
                if (headers != null)
                    foreach (var h in headers)
                        if (!_client.DefaultRequestHeaders.Contains(h.Key))
                            _client.DefaultRequestHeaders.Add(h.Key, h.Value);
                var _response = await _client.GetAsync(prama);
                responseModel.Success = true;
                responseModel.Result = await _response.Content.ReadAsStringAsync();
                responseModel.StatusCode = ((int)_response.StatusCode);
            }
            catch (HttpRequestException _)
            {
                responseModel.Success = false;
                responseModel.Message = _.Message;
                responseModel.StatusCode = ((int)(_?.StatusCode ?? 0));
            }
            return responseModel;
        }

        public async Task<ResponseModel> Post(string prama, object? data, Dictionary<string, string>? headers = null, System.Net.Http.Headers.AuthenticationHeaderValue? auth = null)
        {
            ResponseModel responseModel = new ResponseModel();
            try
            {
                var _client = _factory.CreateClient(_clientName);
                _client.Timeout = TimeSpan.FromMinutes(10);
                _client.BaseAddress = new Uri(_baseUrl);
                _client.DefaultRequestHeaders.Clear();
                //_client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                HttpContent _httpContent = new StringContent(
                    JsonConvert.SerializeObject(data, settings:
                    new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore }));
                _httpContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                if (auth != null) _client.DefaultRequestHeaders.Authorization = auth;
                if (headers != null)
                    foreach (var h in headers)
                        _client.DefaultRequestHeaders.Add(h.Key, h.Value);
                var _response = await _client.PostAsync(prama, _httpContent);
                responseModel.Success = true;
                responseModel.Result = await _response.Content.ReadAsStringAsync();
                responseModel.StatusCode = ((int)_response.StatusCode);
                responseModel.Headers = _response.Headers.ToDictionary(
                    h => h.Key,
                    h => string.Join(",", h.Value)
                    );
            }
            catch (HttpRequestException _)
            {
                responseModel.Success = false;
                responseModel.Message = _.Message;
                responseModel.StatusCode = ((int)(_?.StatusCode ?? 0));
            }
            return responseModel;
        }


        public async Task<ResponseModel> Send(
            string prama,
            HttpMethod? method,
            object? data, Dictionary<string, string>? headers = null,
            System.Net.Http.Headers.AuthenticationHeaderValue? auth = null)
        {
            ResponseModel responseModel = new ResponseModel();
            try
            {
                var _client = _factory.CreateClient(_clientName);
                _client.BaseAddress = new Uri(_baseUrl);
                var request = new HttpRequestMessage(
                    method ?? HttpMethod.Get, $"{prama}");
                if (auth != null)
                {
                    request.Headers.Add("Authorization", auth.ToString());
                    _client.DefaultRequestHeaders.Authorization = auth;
                    _client.DefaultRequestHeaders.ProxyAuthorization = auth;
                }
                if (headers != null)
                    foreach (var h in headers)
                        request.Headers.Add(h.Key, h.Value);
                var content = new StringContent(JsonConvert.SerializeObject(data, settings:
                    new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore }));
                request.Content = content;
                var _response = await _client.SendAsync(request);
                responseModel.Success = true;
                responseModel.Result = await _response.Content.ReadAsStringAsync();
                responseModel.StatusCode = ((int)_response.StatusCode);
            }
            catch (HttpRequestException _)
            {
                responseModel.Success = false;
                responseModel.Message = _.Message;
                responseModel.StatusCode = ((int)(_?.StatusCode ?? 0));
            }
            return responseModel;
        }

        public async Task<ResponseModel> Put(string prama, object? data, Dictionary<string, string>? headers = null, System.Net.Http.Headers.AuthenticationHeaderValue? auth = null)
        {
            ResponseModel responseModel = new ResponseModel();
            try
            {
                var _client = _factory.CreateClient(_clientName);
                _client.Timeout = TimeSpan.FromMinutes(10);
                _client.BaseAddress = new Uri(_baseUrl);
                _client.DefaultRequestHeaders.Clear();
                //_client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                HttpContent _httpContent = new StringContent(
                    JsonConvert.SerializeObject(data, settings:
                    new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore }));
                _httpContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                if (auth != null) _client.DefaultRequestHeaders.Authorization = auth;
                if (headers != null)
                    foreach (var h in headers)
                        _client.DefaultRequestHeaders.Add(h.Key, h.Value);
                var _response = await _client.PutAsync(prama, _httpContent);
                responseModel.Success = true;
                responseModel.Result = await _response.Content.ReadAsStringAsync();
                responseModel.StatusCode = ((int)_response.StatusCode);
            }
            catch (HttpRequestException _)
            {
                responseModel.Success = false;
                responseModel.Message = _.Message;
                responseModel.StatusCode = ((int)(_?.StatusCode ?? 0));
            }
            return responseModel;
        }

        public async Task<ResponseModel> Delete(string prama, object? data, Dictionary<string, string>? headers = null, System.Net.Http.Headers.AuthenticationHeaderValue? auth = null)
        {
            ResponseModel responseModel = new ResponseModel();
            try
            {
                var _client = _factory.CreateClient(_clientName);
                _client.Timeout = TimeSpan.FromMinutes(10);
                _client.BaseAddress = new Uri(_baseUrl);
                _client.DefaultRequestHeaders.Clear();
                //_client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                HttpContent _httpContent = new StringContent(
                    JsonConvert.SerializeObject(data, settings:
                    new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore }));
                _httpContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                if (auth != null) _client.DefaultRequestHeaders.Authorization = auth;
                if (headers != null)
                    foreach (var h in headers)
                        _client.DefaultRequestHeaders.Add(h.Key, h.Value);
                var _response = await _client.DeleteAsync(prama);
                responseModel.Success = true;
                responseModel.Result = await _response.Content.ReadAsStringAsync();
                responseModel.StatusCode = ((int)_response.StatusCode);
            }
            catch (HttpRequestException _)
            {
                responseModel.Success = false;
                responseModel.Message = _.Message;
                responseModel.StatusCode = ((int)(_?.StatusCode ?? 0));
            }
            return responseModel;
        }

        public async Task<bool> Download(string url, string fileName)
        {
            var _client = _factory.CreateClient(_clientName);
            _client.Timeout = TimeSpan.FromMinutes(10);
            _client.BaseAddress = new Uri(_baseUrl);
            var _response = await _client.GetAsync(url);
            Task check = null!;
            try
            {

                using (Stream stream = await _response.Content.ReadAsStreamAsync())
                {
                    using (FileStream _fs = new FileStream(fileName, FileMode.CreateNew))
                    {
                        Task task = stream.CopyToAsync(_fs);
                        check = new Task(() =>
                        {
                            while (!task.IsCompleted) { }
                            return;
                        });
                        task.Wait();
                    }
                }
            }
            catch
            {
                return false;
            }
            return true;
        }

        public async Task<ResponseModel> UploadFile(string url, byte[] content, string fileName, Dictionary<string, string>? data = null, System.Net.Http.Headers.AuthenticationHeaderValue? auth = null)
        {
            ResponseModel responseModel = new ResponseModel();
            try
            {
                var _client = _factory.CreateClient(_clientName);
                _client.BaseAddress = new Uri(_baseUrl);
                var formDataContent = new MultipartFormDataContent();
                _client.DefaultRequestHeaders.Clear();
                formDataContent.Add(new ByteArrayContent(content), fileName);
                if (auth != null) _client.DefaultRequestHeaders.Authorization = auth;
                if (data != null)
                    foreach (var q in data)
                        formDataContent.Add(new StringContent(q.Value), q.Key);
                var _response = await _client.PostAsync(url, formDataContent);
                responseModel.Success = true;
                responseModel.Result = await _response.Content.ReadAsStringAsync();
                responseModel.StatusCode = ((int)_response.StatusCode);
            }
            catch (HttpRequestException _)
            {
                responseModel.Success = false;
                responseModel.Message = _.Message;
                responseModel.StatusCode = ((int)(_?.StatusCode ?? 0));
            }
            return responseModel;
        }

        public async Task<ResponseModel> PutUploadFile(string url, byte[] content, string fileName, Dictionary<string, string>? data = null, System.Net.Http.Headers.AuthenticationHeaderValue? auth = null)
        {
            ResponseModel responseModel = new ResponseModel();
            try
            {
                var _client = _factory.CreateClient(_clientName);
                _client.BaseAddress = new Uri(_baseUrl);
                var formDataContent = new MultipartFormDataContent();
                _client.DefaultRequestHeaders.Clear();
                formDataContent.Add(new ByteArrayContent(content), fileName);
                if (auth != null) _client.DefaultRequestHeaders.Authorization = auth;
                if (data != null)
                    foreach (var q in data)
                        formDataContent.Add(new StringContent(q.Value), q.Key);
                var _response = await _client.PutAsync(url, formDataContent);
                responseModel.Success = true;
                responseModel.Result = await _response.Content.ReadAsStringAsync();
                responseModel.StatusCode = ((int)_response.StatusCode);
            }
            catch (HttpRequestException _)
            {
                responseModel.Success = false;
                responseModel.Message = _.Message;
                responseModel.StatusCode = ((int)(_?.StatusCode ?? 0));
            }
            return responseModel;
        }
    }
}
