﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HttpLogger.Models;
using HttpLogger.Monitors;
using NLog;
using Org.BouncyCastle.Crypto;
using System.Net.Sockets;
using Newtonsoft.Json;

namespace HttpLogger.Services
{
	public class ProxyService
	{
		private static readonly Regex CookieSplitRegEx = new Regex(@",(?! )", RegexOptions.Compiled);
		private const int BufferSize = 8192;

		public Stream ClientStream { get; }

		public SslStream SslStream { get; private set; }

		public StreamReader ClientStreamReader { get; private set; }

		private AsymmetricKeyParameter IssuerKey { get; }

		private ILogger NLogger { get; }

		public HttpWebRequest HttpRequest { get; set; }

		public TcpClient TcpClient { get; }

		public ProxyService(TcpClient client, AsymmetricKeyParameter issuerKey)
		{
			this.TcpClient = client;
			this.ClientStream = client.GetStream();
			this.NLogger = LogManager.GetCurrentClassLogger();
			this.IssuerKey = issuerKey;
		}

		
		public ProxyRequest ProcessRequest()
		{
			var request = new ProxyRequest
			{
				IPAddress = ((IPEndPoint) this.TcpClient.Client.RemoteEndPoint).Address,
				RequestDateTime = DateTime.Now
			};

			// Initialize the request object. populating the data based on the http headers within the client stream
			this.InitializeRequest(request);

			if (!request.SuccessfulInitializaiton)
			{
				return request;
			}

			// create the web request, we are issuing on behalf of the client.
			this.HttpRequest = (HttpWebRequest)WebRequest.Create(request.RemoteUri);
			this.HttpRequest.Method = request.Method;
			this.HttpRequest.ProtocolVersion = request.Version;

			//read the request headers from the client and copy them to our request
			this.ReadRequestHeaders(request);
			
			this.HttpRequest.Proxy = null;
			this.HttpRequest.KeepAlive = false;
			this.HttpRequest.AllowAutoRedirect = false;
			this.HttpRequest.AutomaticDecompression = DecompressionMethods.None;

			return request;
		}



		public void ProcessResponse(ProxyRequest request)
		{
			if (request.Method.ToUpper() == "POST")
			{
				var postBuffer = new char[request.ContentLength];
				int bytesRead;
				var totalBytesRead = 0;
				var sw = new StreamWriter(this.HttpRequest.GetRequestStream());

				while (totalBytesRead < request.ContentLength && (bytesRead = this.ClientStreamReader.ReadBlock(postBuffer, 0, request.ContentLength)) > 0)
				{
					totalBytesRead += bytesRead;
					sw.Write(postBuffer, 0, bytesRead);
				}

				sw.Close();
			}

			this.HttpRequest.Timeout = 15000;

			var response = this.HttpRequest.GetResponse() as HttpWebResponse;

			if (response == null)
			{
				return;
			}

			var responseHeaders = ReadResponseHeaders(response);

			var outStream = request.IsHttps ? this.SslStream : this.ClientStream;

			var myResponseWriter = new StreamWriter(outStream);
			var responseStream = response.GetResponseStream();

			if (responseStream == null)
			{
				response.Close();
				myResponseWriter.Close();
				return;
			}

			try
			{
					
				//send the response status and response headers
				request.StatusCode = response.StatusCode;
				WriteResponseStatus(response.StatusCode, response.StatusDescription, myResponseWriter);
				WriteResponseHeaders(myResponseWriter, responseHeaders);


				var buffer = response.ContentLength > 0 ? new byte[response.ContentLength] : new byte[BufferSize];

				int bytesRead;

				while ((bytesRead = responseStream.Read(buffer, 0, buffer.Length)) > 0)
				{
					outStream.Write(buffer, 0, bytesRead);
				}
				
				outStream.Flush();
			}
			catch (Win32Exception ex)
			{
				// connection was closed by browser/client not an issue.
				if (ex.NativeErrorCode != 10053)
					return;

				this.NLogger.Error(ex);
			}
			catch (Exception ex)
			{
				this.NLogger.Error(ex);
			}
			finally
			{
				responseStream.Close();
				response.Close();
				myResponseWriter.Close();
			}
		}

		#region Private Request Methods

		private void InitializeRequest(ProxyRequest request)
		{
			this.ClientStreamReader = new StreamReader(this.ClientStream);
			var httpCommand = this.ClientStreamReader.ReadLine();

			if (string.IsNullOrEmpty(httpCommand))
			{
				request.SuccessfulInitializaiton = false;
				this.NLogger.Warn("Data header of a proxy request was null or empty.");
				return;
			}
			
			var httpCommandSplit = httpCommand.Split(' ');
			request.Method = httpCommandSplit[0];
			request.RemoteUri = httpCommandSplit[1];
			request.Version = new Version(httpCommandSplit[2].Split('/')[1]);

			if (request.Method == "CONNECT")
			{
				request.SuccessfulInitializaiton = this.SslHandshake(request);
				return;
			}

			request.SuccessfulInitializaiton = true;
		}

		private bool SslHandshake(ProxyRequest request)
		{
			request.IsHttps = true;

			// Browser wants to create a secure tunnel
			// instead we are perform a man in the middle                    
			request.RemoteUri = "https://" + request.RemoteUri;

			var cert = CertificateService.GetSelfSignedCertificate(new Uri(request.RemoteUri), this.IssuerKey);

			//read and ignore headers
			while (!string.IsNullOrEmpty(this.ClientStreamReader.ReadLine()))
			{
			}


			//tell the client that a tunnel has been established
			var connectStreamWriter = new StreamWriter(this.ClientStream);
			connectStreamWriter.WriteLine($"HTTP/{request.Version.ToString(2)} 200 Connection established");
			connectStreamWriter.WriteLine
				($"Timestamp: {DateTime.Now.ToString(CultureInfo.InvariantCulture)}");
			connectStreamWriter.WriteLine("Proxy-agent: http-logger.net");
			connectStreamWriter.WriteLine();
			connectStreamWriter.Flush();

			//now-create an https "server"
			this.SslStream = new SslStream(this.ClientStream, false);
			this.SslStream.AuthenticateAsServer(cert,
				false, SslProtocols.Tls | SslProtocols.Ssl3 | SslProtocols.Ssl2, true);

			//HTTPS server created - we can now decrypt the client's traffic
			this.ClientStreamReader = new StreamReader(this.SslStream);

			//read the new http command.
			var httpCommand = this.ClientStreamReader.ReadLine();
			
			if (string.IsNullOrEmpty(httpCommand))
			{
				
				this.ClientStreamReader.Close();
				this.ClientStream.Close();
				this.SslStream.Close();
				return false;
			}

			var httpCommandSplit = httpCommand.Split(' ');
			request.Method = httpCommandSplit[0];
			request.RemoteUri += httpCommandSplit[1];
			return true;
		}

		private void ReadRequestHeaders(ProxyRequest request)
		{
			string httpCmd;
			request.ContentLength = 0;

			do
			{
				httpCmd = this.ClientStreamReader.ReadLine();
				if (string.IsNullOrEmpty(httpCmd))
				{
					return;
				}
				var header = httpCmd.Split(new[] { ": " }, 2, StringSplitOptions.None);
				switch (header[0].ToLower())
				{
					case "host":
						this.HttpRequest.Host = header[1];
						break;
					case "user-agent":
						this.HttpRequest.UserAgent = header[1];
						break;
					case "accept":
						this.HttpRequest.Accept = header[1];
						break;
					case "referer":
						this.HttpRequest.Referer = header[1];
						break;
					case "cookie":
						this.HttpRequest.Headers["Cookie"] = header[1];
						break;
					case "proxy-connection":
					case "connection":
					case "keep-alive":
					case "100-continue":
						//ignore
						break;
					case "content-length":
						int contentLength;
						int.TryParse(header[1], out contentLength);
						request.ContentLength = contentLength;
						break;
					case "content-type":
						this.HttpRequest.ContentType = header[1];
						break;
					case "if-modified-since":
						var sb = header[1].Trim().Split(';');
						DateTime d;
						if (DateTime.TryParse(sb[0], out d))
							this.HttpRequest.IfModifiedSince = d;
						break;
					case "expect":
						this.HttpRequest.Expect = header[1];
						break;
					default:
						try
						{
							this.HttpRequest.Headers.Add(header[0], header[1]);
						}
						catch (Exception ex)
						{
							this.NLogger.Error($"Could not add header {header[0]}, value: {header[1]}.  Exception message:{ex.Message}");
						}
						break;
				}
			} while (!string.IsNullOrWhiteSpace(httpCmd));

		}
		#endregion

		#region Private Response Methods

		private static void WriteResponseStatus(HttpStatusCode code, string description, StreamWriter myResponseWriter)
		{
			var s = $"HTTP/1.0 {(int)code} {description}";
			myResponseWriter.WriteLine(s);
		}

		private static void WriteResponseHeaders(StreamWriter myResponseWriter, List<Tuple<string, string>> headers)
		{
			if (headers != null)
			{
				foreach (Tuple<string, string> header in headers)
					myResponseWriter.WriteLine($"{header.Item1}: {header.Item2}");
			}
			myResponseWriter.WriteLine();
			myResponseWriter.Flush();
		}

		private static List<Tuple<string, string>> ReadResponseHeaders(HttpWebResponse response)
		{
			string value = null;
			string header = null;
			var returnHeaders = new List<Tuple<string, string>>();
			foreach (string s in response.Headers.Keys)
			{
				if (s.ToLower() == "set-cookie")
				{
					header = s;
					value = response.Headers[s];
				}
				else
					returnHeaders.Add(new Tuple<string, string>(s, response.Headers[s]));
			}

			if (!string.IsNullOrWhiteSpace(value))
			{
				response.Headers.Remove(header);
				var cookies = CookieSplitRegEx.Split(value);
				returnHeaders.AddRange(cookies.Select(cookie => new Tuple<string, string>("Set-Cookie", cookie)));
			}
			returnHeaders.Add(new Tuple<string, string>("X-Proxied-By", "http-logger.net"));
			return returnHeaders;
		}

		#endregion

	}
}
