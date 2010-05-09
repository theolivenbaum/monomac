//
// Copyright 2010, Novell, Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
using System;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using MonoMac.Foundation;
using MonoMac.ObjCRuntime;

namespace MonoMac.ObjCRuntime {

	public static class Runtime {
		static List <Assembly> assemblies;
		static Dictionary <IntPtr, WeakReference> object_map = new Dictionary <IntPtr, WeakReference> ();
		static object lock_obj = new object ();
		static IntPtr selClass = Selector.GetHandle ("class");
		
		public static void RegisterAssembly (Assembly a) {
			if (assemblies == null) {
				assemblies = new List <Assembly> ();
				Class.Register (typeof (NSObject));
			}

			assemblies.Add (a);

			foreach (Type type in a.GetTypes ()) {
				if (type.IsSubclassOf (typeof (NSObject)) && !Attribute.IsDefined (type, typeof (ModelAttribute), false))
					Class.Register (type);
			}
		}

		internal static List<Assembly> GetAssemblies () {
			if (assemblies == null) {
				assemblies = new List <Assembly> ();
				foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies ())
					assemblies.Add (a);
			}

			return assemblies;
		}

		internal static void UnregisterNSObject (IntPtr ptr) {
			lock (lock_obj) {
				object_map.Remove (ptr);
			}
		}

		internal static void RegisterNSObject (NSObject obj, IntPtr ptr) {
			lock (lock_obj) {
				object_map [ptr] = new WeakReference (obj);
				obj.Handle = ptr;
			}
		}

		public static NSObject GetNSObject (IntPtr ptr) {
			Type type;

			if (ptr == IntPtr.Zero)
				return null;

			lock (lock_obj) {
				WeakReference reference;
				if (object_map.TryGetValue (ptr, out reference))
					if (reference.IsAlive)
						return (NSObject) reference.Target;
			}
			
			type = Class.Lookup (Messaging.intptr_objc_msgSend (ptr, selClass));

			if (type != null) {
				return (NSObject) Activator.CreateInstance (type, new object[] { ptr });
			} else {
				Console.WriteLine ("WARNING: Cannot find type for {0} ({1}) using NSObject", new Class (ptr).Name, ptr);
				return new NSObject (ptr);
			}
		}
	}

	[StructLayout (LayoutKind.Sequential)]
	public struct BlockDescriptor {
		public int reserved;
		public int size;
		public IntPtr copy_helper;
		public IntPtr dispose;
	}

	[StructLayout (LayoutKind.Sequential)]
	public struct BlockLiteral {
		public IntPtr isa;
		public int flags;
		public int reserved;
		public IntPtr invoke;
		public IntPtr block_descriptor;
		public IntPtr handle;

		internal static IntPtr MonoTouchDescriptor;

		//
		// trampoline must be static, and someone else needs to keep a ref to it
		//
		public static unsafe IntPtr CreateBlock (Delegate trampoline, Delegate userDelegate)
		{
			if (MonoTouchDescriptor == IntPtr.Zero){
				var desc = Marshal.AllocHGlobal (sizeof (BlockDescriptor));
				Marshal.WriteInt32 (desc, 4, sizeof (BlockLiteral));
				MonoTouchDescriptor = desc;
			}
			
			var block = (BlockLiteral *) Marshal.AllocHGlobal (sizeof (BlockLiteral));
			block->block_descriptor = MonoTouchDescriptor;
			block->isa = Class.GetHandle ("__NSConcreteGlobalBlock");
			block->invoke = Marshal.GetFunctionPointerForDelegate (trampoline);

			// BLOCK_IS_GLOBAL, maybe add later BLOCK_HAS_COPY_DISPOSE (1 << 25)
			block->flags = 1 << 28;
			block->handle = (IntPtr) GCHandle.Alloc (userDelegate);
			
			return (IntPtr) block;
		}
	}
}