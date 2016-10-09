/* part of Pyrolite, by Irmen de Jong (irmen@razorvine.net) */

using System;
using Razorvine.Pyro;

namespace Pyrolite.TestPyroStreaming
{

/// <summary>
/// Test Pyro with streaming.
/// </summary>
public class TestStreaming {

	static protected byte[] hmacKey = null;

	public static void XXXXXXMain(String[] args)  {
		try {
			Test();
		} catch (Exception x) {
			Console.WriteLine("unhandled exception: {0}",x);
		}
		Console.WriteLine("\r\nEnter to exit:"); Console.ReadLine();
	}
	
	public static void Test() {

		setConfig();
		// Config.SERIALIZER = Config.SerializerType.pickle;

		Console.WriteLine("Pyrolite version: "+Config.PYROLITE_VERSION);
		Console.Write("Enter stream server URI: ");
		string uri = Console.ReadLine();

		using(dynamic p = new PyroProxy(new PyroURI(uri.Trim()))) {
			
			Console.WriteLine("LIST:");
			dynamic result = p.list();
			Console.WriteLine(result);
			foreach(int i in result)
			{
				Console.WriteLine(i);
			}
			
			Console.WriteLine("ITERATOR:");
			using(result = p.iterator())
			{
				Console.WriteLine(result);
				foreach(int i in result)
				{
					Console.WriteLine(i);
				}
			}

			Console.WriteLine("GENERATOR:");
			result = p.generator();
			Console.WriteLine(result);
			foreach(int i in result)
			{
				Console.WriteLine(i);
			}

			Console.WriteLine("SLOW GENERATOR:");
			using(result = p.slow_generator())
			{
				Console.WriteLine(result);
				foreach(int i in result)
				{
					Console.WriteLine(i);
					if(i>5) {
						p.Dispose();
					}
				}
			}
		}

	}
	
	static void setConfig()
	{
		string tracedir=Environment.GetEnvironmentVariable("PYRO_TRACE_DIR");
		if(tracedir!=null) {
			Config.MSG_TRACE_DIR=tracedir;
		}
		string serializer=Environment.GetEnvironmentVariable("PYRO_SERIALIZER");
		if(serializer!=null) {
			Config.SERIALIZER=(Config.SerializerType) Enum.Parse(typeof(Config.SerializerType), serializer, true);
		}
	}
}

}

