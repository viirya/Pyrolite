/* part of Pyrolite, by Irmen de Jong (irmen@razorvine.net) */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace Razorvine.Pyro
{

/// <summary>
/// Proxy for Pyro objects.
/// If you declare it as a 'PyroProxy' variable, you have to use the call() method to invoke remote methods.
/// If you declare it as a 'dynamic' variable, you can just invoke the remote methods on it directly, and access remote attributes directly.
/// </summary>
[Serializable]
public class PyroProxy : DynamicObject, IDisposable {

	public string hostname {get;set;}
	public int port {get;set;}
	public string objectid {get;set;}
	public byte[] pyroHmacKey = null;		// per-proxy hmac key, used to be HMAC_KEY config item
	public Guid? correlation_id = null;     // per-proxy correlation id (need to set/update this yourself)
	public object pyroHandshake = "hello";	// data object that should be sent in the initial connection handshake message. Can be any serializable object.

	private ushort sequenceNr = 0;
	private TcpClient sock;
	private NetworkStream sock_stream;
	
	public ISet<string> pyroMethods = new HashSet<string>();	// remote methods
	public ISet<string> pyroAttrs = new HashSet<string>();	// remote attributes
	public ISet<string> pyroOneway = new HashSet<string>();	// oneway methods

	/// <summary>
	/// No-args constructor for (un)pickling support
	/// </summary>
	public PyroProxy() {
	}

	/// <summary>
	/// Create a proxy for the remote Pyro object denoted by the uri
	/// </summary>
	public PyroProxy(PyroURI uri) : this(uri.host, uri.port, uri.objectid) {
	}

	/// <summary>
	/// Create a proxy for the remote Pyro object on the given host and port, with the given objectid/name.
	/// </summary>
	public PyroProxy(string hostname, int port, string objectid) {
		this.hostname = hostname;
		this.port = port;
		this.objectid = objectid;
	}

	/// <summary>
	/// Release resources when descructed
	/// </summary>
	~PyroProxy() {
		this.close();
	}

	public void Dispose() {
		close();
	}

	/// <summary>
	/// (re)connect the proxy to the remote Pyro daemon.
	/// </summary>
	protected void connect() {
		if (sock == null) {
			sock = new TcpClient();
			sock.Connect(hostname,port);
			sock.NoDelay=true;
			sock_stream=sock.GetStream();
			sequenceNr = 0;
			_handshake();

			if (Config.METADATA) {
				// obtain metadata if this feature is enabled, and the metadata is not known yet
				if (pyroMethods.Any() || pyroAttrs.Any()) {
					// not checking _pyroOneway because that feature already existed and people are already modifying it on the proxy
					// log.debug("reusing existing metadata")
				} else {
					GetMetadata(this.objectid);
				}
			}
		}
	}

	/// <summary>
	/// get metadata from server (methods, attrs, oneway, ...) and remember them in some attributes of the proxy
	/// </summary>
	protected void GetMetadata(string objectId) {
		// get metadata from server (methods, attrs, oneway, ...) and remember them in some attributes of the proxy
		objectId = objectId ?? this.objectid;
		if(sock==null) {
			connect();
			if(pyroMethods.Any() || pyroAttrs.Any())
				return;    // metadata has already been retrieved as part of creating the connection
		}
	
		//  invoke the get_metadata method on the daemon
		Hashtable result = this.internal_call("get_metadata", Config.DAEMON_NAME, 0, false, new [] {objectId}) as Hashtable;
		if(result==null)
			return;
		_processMetadata(result);
	}

	/// <summary>
	/// Extract meta data and store it in the relevant properties on the proxy.
	/// If no attribute or method is exposed at all, throw an exception.
	/// </summary>
	private void _processMetadata(Hashtable result)
	{
		// the collections in the result can be either an object[] or a HashSet<object> or List<object>, 
		// depending on the serializer and Pyro version that is used
		object[] methods_array = result["methods"] as object[];
		object[] attrs_array = result["attrs"] as object[];
		object[] oneway_array = result["oneway"] as object[];
		
		this.pyroMethods = methods_array != null ? new HashSet<string>(methods_array.Select(o => o as string)) : GetStringSet(result["methods"]);
		this.pyroAttrs = attrs_array != null ? new HashSet<string>(attrs_array.Select(o => o as string)) : GetStringSet(result["attrs"]);
		this.pyroOneway = oneway_array != null ? new HashSet<string>(oneway_array.Select(o => o as string)) : GetStringSet(result["attrs"]);
		
		if(!pyroMethods.Any() && !pyroAttrs.Any()) {
			throw new PyroException("remote object doesn't expose any methods or attributes");
		}
	}
	
	protected HashSet<string> GetStringSet(object strings)
	{
		var result1 = strings as HashSet<string>;
		if(result1!=null)
			return result1;
		
		// another collection, convert to set of strings.
		var result2 = (IEnumerable) strings;
		var stringset = new HashSet<string>();
		foreach(object s in result2) {
			stringset.Add(s.ToString());
		}
		return stringset;
	}

	/// <summary>
	/// Makes it easier to call methods on the proxy by intercepting the methods calls.
	/// You'll have to use the 'dynamic' type for your proxy object though.
	/// </summary>
	public override bool TryInvokeMember(InvokeMemberBinder binder, object[] args, out object result)
	{
		result = call(binder.Name, args);
		return true;
	}

	// dynamic attribute retrieval
	public override bool TryGetMember(GetMemberBinder binder, out object result)
	{
		result = getattr(binder.Name);
		return true;
	}

	// dynamic attribute setting
	public override bool TrySetMember(SetMemberBinder binder, object value)
	{
		setattr(binder.Name, value);
		return true;
	}
	
	    
	/// <summary>
	/// Call a method on the remote Pyro object this proxy is for.
	/// </summary>
	/// <param name="method">the name of the method you want to call</param>
	/// <param name="arguments">zero or more arguments for the remote method</param>
	/// <returns>the result Object from the remote method call (can be anything, you need to typecast/introspect yourself).</returns>
	public object call(string method, params object[] arguments) {
		return internal_call(method, null, 0, true, arguments);
	}

	/// <summary>
	/// Call a method on the remote Pyro object this proxy is for, using Oneway call semantics (return immediately).
	/// (obsolete: just use call.)
	/// </summary>
	/// <param name="method">the name of the method you want to call</param>
	/// <param name="arguments">zero or more arguments for the remote method</param>
	[Obsolete("Pyro now figures out automatically what methods are oneway by getting metadata from the server. Just use call().")]
	public void call_oneway(string method, params object[] arguments) {
		internal_call(method, null, Message.FLAGS_ONEWAY, true, arguments);
	}

	/// <summary>
	/// Get the value of a remote attribute.
	/// </summary>
	/// <param name="attr">the attribute name</param>
	public object getattr(string attr) {
		return this.internal_call("__getattr__", null, 0, false, new object[]{attr});
	}
	
	
	/// <summary>
	/// Set a new value on a remote attribute.
	/// </summary>
	/// <param name="attr">the attribute name</param>
	/// <param name="value">the new value for the attribute</param>
	public void setattr(string attr, object value) {
		this.internal_call("__setattr__", null, 0, false, new object[] {attr, value});
	}
	
	/// <summary>
	/// Returns a dict with annotations to be sent with each message.
    /// Default behavior is to include the current correlation id (if it is set).
    /// (note that the Message may include an additional hmac annotation at the moment the message is sent)
	/// </summary>
	public virtual IDictionary<string, byte[]> annotations()
	{
		var ann = new Dictionary<string, byte[]>(0);
		if(correlation_id.HasValue) {
			ann["CORR"]=correlation_id.Value.ToByteArray();
		}
		return ann;
	}
	
	/// <summary>
	/// Internal call method to actually perform the Pyro method call and process the result.
	/// </summary>
	private object internal_call(string method, string actual_objectId, ushort flags, bool checkMethodName, params object[] parameters) {
		actual_objectId = actual_objectId ?? this.objectid;
		lock(this) {
			connect();
			unchecked {
			    sequenceNr++;        // unchecked so this ushort wraps around 0-65535 instead of raising an OverflowException
			}
		}
		if(pyroAttrs.Contains(method)) {
			throw new PyroException("cannot call an attribute");
		}
		if(pyroOneway.Contains(method)) {
			flags |= Message.FLAGS_ONEWAY;
		}
		if(checkMethodName && Config.METADATA && !pyroMethods.Contains(method)) {
			throw new PyroException(string.Format("remote object '{0}' has no exposed attribute or method '{1}'", actual_objectId, method));
		}

		if (parameters == null)
			parameters = new object[] {};
		
		PyroSerializer ser = PyroSerializer.GetFor(Config.SERIALIZER);
		byte[] pickle = ser.serializeCall(actual_objectId, method, parameters, new Dictionary<string,object>(0));
		var msg = new Message(Message.MSG_INVOKE, pickle, ser.serializer_id, flags, sequenceNr, this.annotations(), pyroHmacKey);
		Message resultmsg;
		lock (this.sock) {
			IOUtil.send(sock_stream, msg.to_bytes());
			if(Config.MSG_TRACE_DIR!=null) {
				Message.TraceMessageSend(sequenceNr, msg.get_header_bytes(), msg.get_annotations_bytes(), msg.data);
			}
			pickle = null;

			if ((flags & Message.FLAGS_ONEWAY) != 0)
				return null;

			resultmsg = Message.recv(sock_stream, new ushort[]{Message.MSG_RESULT}, pyroHmacKey);
		}
		if (resultmsg.seq != sequenceNr) {
			throw new PyroException("result msg out of sync");
		}
		responseAnnotations(resultmsg.annotations, resultmsg.type);
		if ((resultmsg.flags & Message.FLAGS_COMPRESSED) != 0) {
			_decompressMessageData(resultmsg);
		}

		if ((resultmsg.flags & Message.FLAGS_ITEMSTREAMRESULT) != 0) {
			byte[] streamId = null;
			if(!resultmsg.annotations.TryGetValue("STRM", out streamId)) {
				throw new PyroException("result of call is an iterator, but the server is not configured to allow streaming");
			}
			return new PyroProxy.StreamResultIterator(Encoding.UTF8.GetString(streamId), this);
		}
		
		if ((resultmsg.flags & Message.FLAGS_EXCEPTION) != 0) {
			Exception rx = (Exception) ser.deserializeData(resultmsg.data);
			if (rx is PyroException) {
				throw (PyroException) rx;
			} else {
				PyroException px = new PyroException("remote exception occurred", rx);
				PropertyInfo remotetbProperty=rx.GetType().GetProperty("_pyroTraceback");
				if(remotetbProperty!=null) {
					string remotetb=(string)remotetbProperty.GetValue(rx,null);
					px._pyroTraceback=remotetb;
				}
				throw px;
			}
		}
		
		return ser.deserializeData(resultmsg.data);
	}

	
	/// <summary>
	/// Decompress the data bytes in the given message (in place).
	/// </summary>
	private void _decompressMessageData(Message msg) {
		if((msg.flags & Message.FLAGS_COMPRESSED) == 0) {
			throw new ArgumentException("message data is not compressed");
		}
		using(MemoryStream compressed=new MemoryStream(msg.data, 2, msg.data.Length-2, false)) {
			using(DeflateStream decompresser=new DeflateStream(compressed, CompressionMode.Decompress)) {
				MemoryStream bos = new MemoryStream(msg.data.Length);
        		byte[] buffer = new byte[4096];
        		int numRead;
        		while ((numRead = decompresser.Read(buffer, 0, buffer.Length)) != 0) {
        		    bos.Write(buffer, 0, numRead);
        		}
        		msg.data=bos.ToArray();
        		msg.flags ^= Message.FLAGS_COMPRESSED;
			}
		}
	}
		
	/// <summary>
	/// Close the network connection of this Proxy.
	/// If you re-use the proxy, it will automatically reconnect.
	/// </summary>
	public void close() {
		if (this.sock != null) {
			if(this.sock_stream!=null) this.sock_stream.Close();
			this.sock.Client.Close();
			this.sock.Close();
			this.sock=null;
			this.sock_stream=null;
		}
	}

	/// <summary>
	/// Perform the Pyro protocol connection handshake with the Pyro daemon.
	/// </summary>
	protected void _handshake() {
		var ser = PyroSerializer.GetFor(Config.SERIALIZER);
		var handshakedata = new Dictionary<string, Object>();
		handshakedata["handshake"] = pyroHandshake;
		if(Config.METADATA)
			handshakedata["object"] = objectid;
		byte[] data = ser.serializeData(handshakedata);
		ushort flags = Config.METADATA? Message.FLAGS_META_ON_CONNECT : (ushort)0;
		var msg = new Message(Message.MSG_CONNECT, data, ser.serializer_id, flags, sequenceNr, annotations(), pyroHmacKey);
		IOUtil.send(sock_stream, msg.to_bytes());
		if(Config.MSG_TRACE_DIR!=null) {
			Message.TraceMessageSend(sequenceNr, msg.get_header_bytes(), msg.get_annotations_bytes(), msg.data);
		}
		
		// process handshake response
		msg = Message.recv(sock_stream, new ushort[]{Message.MSG_CONNECTOK, Message.MSG_CONNECTFAIL}, pyroHmacKey);
		responseAnnotations(msg.annotations, msg.type);
		object handshake_response = "?";
		if(msg.data!=null) {
			if((msg.flags & Message.FLAGS_COMPRESSED) != 0) {
				_decompressMessageData(msg);
			}
			try {
				ser = PyroSerializer.GetFor(msg.serializer_id);
				handshake_response = ser.deserializeData(msg.data);	
			} catch (Exception) {
				msg.type = Message.MSG_CONNECTFAIL;
				handshake_response = "<not available because unsupported serialization format>";
			}
		}
		if(msg.type==Message.MSG_CONNECTOK) {
			if((msg.flags & Message.FLAGS_META_ON_CONNECT) != 0) {
				var response_dict = (Hashtable)handshake_response;
				_processMetadata(response_dict["meta"] as Hashtable);
				handshake_response = response_dict["handshake"];
				try {
					validateHandshake(handshake_response);
				} catch (Exception) {
					close();
					throw;
				}
			}
		} else if (msg.type==Message.MSG_CONNECTFAIL) {
			close();
			throw new PyroException("connection rejected, reason: "+handshake_response);
		} else {
			close();
			throw new PyroException(string.Format("connect: invalid msg type {0} received", msg.type));
		}
	}
	
	/// <summary>
	/// Process and validate the initial connection handshake response data received from the daemon.
	/// Simply return without error if everything is ok.
    /// Throw an exception if something is wrong and the connection should not be made.
	/// </summary>
	public virtual void validateHandshake(object handshake_response)
	{
		// override this in subclass
	}
	
	/// <summary>
	/// Process any response annotations (dictionary set by the daemon).
	/// Usually this contains the internal Pyro annotations such as hmac and correlation id,
	/// and if you override the annotations method in the daemon, can contain your own annotations as well.
	/// </summary>
	public virtual void responseAnnotations(IDictionary<string, byte[]> annotations, ushort msgtype)
	{
		// override this in subclass
	}
	
	/// <summary>
	/// called by the Unpickler to restore state.
	/// </summary>
	/// <param name="args">args(8): pyroUri, pyroOneway(hashset), pyroMethods(set), pyroAttrs(set), pyroTimeout, pyroHmacKey, pyroHandshake, pyroMaxRetries</param>
	public void __setstate__(object[] args) {
		if(args.Length != 8) {
			throw new PyroException("invalid pickled proxy, using wrong pyro version?");
		}
		PyroURI uri=(PyroURI)args[0];
		this.hostname=uri.host;
		this.port=uri.port;
		this.objectid=uri.objectid;
		this.sock=null;
		this.sock_stream=null;
		this.correlation_id=null;
		
		this.pyroOneway.Clear();
		foreach(object o in (HashSet<object>) args[1])
			this.pyroOneway.Add(o as string);
		this.pyroMethods.Clear();
		foreach(object o in (HashSet<object>) args[2])
			this.pyroMethods.Add(o as string);
		this.pyroAttrs.Clear();
		foreach(object o in (HashSet<object>) args[3])
			this.pyroAttrs.Add(o as string);

		this.pyroHmacKey = (byte[])args[5];
		this.pyroHandshake = args[6];
		// maxretries is not yet supported/used in pyrolite
	}	
	
	public class StreamResultIterator: IEnumerable, IDisposable
	{
		private string streamId;
		private PyroProxy proxy;
		public StreamResultIterator(string streamId, PyroProxy proxy)
		{
			this.streamId = streamId;
			this.proxy = proxy;
		}

		public IEnumerator GetEnumerator()
		{
			while(true) {
				if(this.proxy.sock ==null) {
					throw new PyroException("the proxy for this stream result has been closed");
				}
				object value = null;
				try {
					value = this.proxy.internal_call("get_next_stream_item", Config.DAEMON_NAME, 0, false, new [] {this.streamId});
				} catch (PyroException x) {
					if(x._pythonExceptionType=="builtins.StopIteration" || x._pythonExceptionType=="exceptions.StopIteration" ||
					   x._pythonExceptionType=="builtins.StopAsyncIteration" || 
					   x._pythonExceptionType=="builtins.GeneratorExit" || x._pythonExceptionType=="exceptions.GeneratorExit") {
						// iterator ended normally.
						this.Dispose();
						yield break;
					}
					throw;  
				}
				yield return value;
			}
		}
		
		public void Dispose()
		{
			if(this.proxy!=null && this.proxy.sock!=null) {
				this.proxy.internal_call("close_stream", Config.DAEMON_NAME, Message.FLAGS_ONEWAY, false, new [] {this.streamId});
			}
			this.proxy = null;
		}
	}
}

}
