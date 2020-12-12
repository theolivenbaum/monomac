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
using System.Reflection.Emit;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Linq;

using MonoMac.Foundation;

namespace MonoMac.ObjCRuntime
{
    internal abstract class NativeImplementationBuilder
    {
        internal static AssemblyBuilder builder;
        internal static ModuleBuilder module;

#if !MONOMAC_BOOTSTRAP
        private static MethodInfo convertarray = typeof(NSArray).GetMethod("ArrayFromHandle", new Type[] { typeof(IntPtr) });
        private static MethodInfo convertsarray = typeof(NSArray).GetMethod("StringArrayFromHandle", new Type[] { typeof(IntPtr) });
        private static MethodInfo convertstring = typeof(NSString).GetMethod("ToString", Type.EmptyTypes);
        private static MethodInfo getobject = typeof(Runtime).GetMethods().First(m => m.Name == "GetNSObject" && !m.IsGenericMethod);
        private static MethodInfo gethandle = typeof(NSObject).GetMethod("get_Handle", BindingFlags.Instance | BindingFlags.Public);
        private static FieldInfo intptrzero = typeof(IntPtr).GetField("Zero", BindingFlags.Static | BindingFlags.Public);
#endif

        private Delegate del;

        static NativeImplementationBuilder()
        {
#if NETSTANDARD
            builder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName { Name = "ObjCImplementations" }, AssemblyBuilderAccess.Run);
            module = builder.DefineDynamicModule("Implementations");
#else
            builder = AppDomain.CurrentDomain.DefineDynamicAssembly (new AssemblyName {Name = "ObjCImplementations"}, AssemblyBuilderAccess.Run, null, true, null);
			module = builder.DefineDynamicModule ("Implementations", false);
#endif
        }

        internal abstract Delegate CreateDelegate();

        internal int ArgumentOffset
        {
            get; set;
        }

        internal IntPtr Selector
        {
            get; set;
        }

        internal Type[] ParameterTypes
        {
            get; set;
        }

        internal ParameterInfo[] Parameters
        {
            get; set;
        }

        internal Delegate Delegate
        {
            get
            {
                if (del == null)
                    del = CreateDelegate();

                return del;
            }
        }

        internal Type DelegateType
        {
            get; set;
        }

        internal string Signature
        {
            get; set;
        }

        protected Type CreateDelegateType(Type return_type)
        {
			var argument_types = ParameterTypes;

            TypeBuilder type = module.DefineType(Guid.NewGuid().ToString(), TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.AnsiClass | TypeAttributes.AutoClass, typeof(MulticastDelegate));

			// doesn't work on .NET Core.. not sure why, but it works without..
			//type.SetCustomAttribute(new CustomAttributeBuilder(typeof(MarshalAsAttribute).GetConstructor(new Type[] { typeof(UnmanagedType) }), new object[] { UnmanagedType.FunctionPtr }));

			ConstructorBuilder constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new Type[] { typeof(object), typeof(int) });

            constructor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

            MethodBuilder method = null;

            method = type.DefineMethod("Invoke", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot, return_type, argument_types);

            if (NeedsCustomMarshaler(return_type))
                SetupParameter(method, 0, return_type);

            for (int i = 1; i <= argument_types.Length; i++)
			{
                if (NeedsCustomMarshaler(argument_types[i - 1]))
                    SetupParameter(method, i, argument_types[i - 1]);
			}

            method.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

#if NETSTANDARD
            return type.CreateTypeInfo().AsType();
#else
            return type.CreateType ();
#endif
		}

		private bool NeedsCustomMarshaler (Type t) {
			if (t == typeof (NSObject) || t.IsSubclassOf (typeof (NSObject)))
				return true;
			if (t == typeof (Selector))
				return true;

			return false;
		}

		private Type MarshalerForType (Type t) {
			if (t == typeof (NSObject) || t.IsSubclassOf (typeof (NSObject)))
				return typeof (NSObjectMarshaler<>).MakeGenericType (t);
			if (t == typeof (Selector))
				return typeof (SelectorMarshaler);

			throw new ArgumentException ("Cannot determine marshaler type for: " + t);
		}
		static ConstructorInfo cinfo = typeof (MarshalAsAttribute).GetConstructor (new Type[] { typeof (UnmanagedType) });
		static FieldInfo[] mtrfld = new FieldInfo [] { typeof (MarshalAsAttribute).GetField ("MarshalTypeRef") };
		static object[] customMarshalerArgs = new object [] { UnmanagedType.CustomMarshaler };

		private void SetupParameter (MethodBuilder builder, int index, Type t) {
			var marshaler = MarshalerForType (t);

			ParameterBuilder pb = builder.DefineParameter (index, ParameterAttributes.HasFieldMarshal, string.Format ("arg{0}", index));
			CustomAttributeBuilder cabuilder = new CustomAttributeBuilder (cinfo, customMarshalerArgs, mtrfld, new object [] { marshaler });

			pb.SetCustomAttribute (cabuilder);
		}

		protected bool IsWrappedType (Type type) {
			if (type == typeof (NSObject) || type.IsSubclassOf (typeof (NSObject)) || type == typeof (string))
				return true;

			return false;
		}

		protected void ConvertParameters (ParameterInfo [] parms, bool isstatic, bool isstret) {
			if (isstret) {
				ArgumentOffset = 3;
				ParameterTypes = new Type [ArgumentOffset + parms.Length];
				ParameterTypes [0] = typeof (IntPtr);
				ParameterTypes [1] = isstatic ? typeof (IntPtr) : typeof (NSObject);
				ParameterTypes [2] = typeof (IntPtr);
			} else {
				ArgumentOffset = 2;
				ParameterTypes = new Type [ArgumentOffset + parms.Length];
				ParameterTypes [0] = isstatic ? typeof (IntPtr) : typeof (NSObject);
				ParameterTypes [1] = typeof (IntPtr);
			}

			for (int i = 0; i < Parameters.Length; i++) {
				var parameter = Parameters[i];
				if (parameter.ParameterType.IsByRef && IsWrappedType (parameter.ParameterType.GetElementType ()))
					ParameterTypes [i + ArgumentOffset] = typeof (IntPtr).MakeByRefType ();
				else if (parameter.ParameterType.IsArray && IsWrappedType (parameter.ParameterType.GetElementType ()))
					ParameterTypes [i + ArgumentOffset] = typeof (IntPtr);
				else if (typeof (INativeObject).IsAssignableFrom (parameter.ParameterType) && !IsWrappedType (parameter.ParameterType))
					ParameterTypes [i + ArgumentOffset] = typeof (IntPtr);
				else if (parameter.ParameterType == typeof (string))
					ParameterTypes [i + ArgumentOffset] = typeof (NSString);
				else if (parameter.GetCustomAttribute<BlockProxyAttribute>() != null)
				{
					ParameterTypes [i + ArgumentOffset] = typeof (IntPtr);
				}
				else
				{
					ParameterTypes [i + ArgumentOffset] = parameter.ParameterType;
				}
				// The TypeConverter will emit a ^@ for a byref type that is a NSObject or NSObject subclass in this case
				// If we passed the ParameterTypes [i+ArgumentOffset] as would be more logical we would emit a ^^v for that case, which
				// while currently acceptible isn't representative of what obj-c wants.
				Signature += TypeConverter.ToNative (parameter.ParameterType);
			}
		}

		protected void DeclareLocals (ILGenerator il) {
			// Keep in sync with UpdateByRefArguments()
			for (int i = 0; i < Parameters.Length; i++) {
				var parameter = Parameters[i];
				if (parameter.ParameterType.IsByRef && IsWrappedType (parameter.ParameterType.GetElementType ())) {
					il.DeclareLocal (parameter.ParameterType.GetElementType ());
				} else if (parameter.ParameterType.IsArray && IsWrappedType (parameter.ParameterType.GetElementType ())) {
					il.DeclareLocal (parameter.ParameterType);
				} else if (parameter.ParameterType == typeof (string)) {
					il.DeclareLocal (typeof (string));
				} else if (parameter.GetCustomAttribute<BlockProxyAttribute>() != null) {
					il.DeclareLocal (parameter.ParameterType);
				}
			}
		}


		protected void ConvertArguments (ILGenerator il, int locoffset) {
#if !MONOMAC_BOOTSTRAP
			for (int i = ArgumentOffset, j = 0; i < ParameterTypes.Length; i++) {
				var parameter = Parameters[i - ArgumentOffset];
				if (parameter.ParameterType.IsByRef && (Attribute.GetCustomAttribute (parameter, typeof (OutAttribute)) == null) && IsWrappedType (parameter.ParameterType.GetElementType ())) {
					var nullout = il.DefineLabel ();
					var done = il.DefineLabel ();
					il.Emit (OpCodes.Ldarg, i);
					il.Emit (OpCodes.Brfalse, nullout);
					il.Emit (OpCodes.Ldarg, i);
					il.Emit (OpCodes.Ldind_I);
					il.Emit (OpCodes.Call, getobject);
					il.Emit (OpCodes.Br, done);
					il.MarkLabel (nullout);
					il.Emit (OpCodes.Ldnull);
					il.MarkLabel (done);
					il.Emit (OpCodes.Stloc, j+locoffset);
					j++;
				} else if (parameter.ParameterType.IsArray && IsWrappedType (parameter.ParameterType.GetElementType ())) {
					var nullout = il.DefineLabel ();
					var done = il.DefineLabel ();
					il.Emit (OpCodes.Ldarg, i);
					il.Emit (OpCodes.Brfalse, nullout);
					il.Emit (OpCodes.Ldarg, i);
					if (parameter.ParameterType.GetElementType () == typeof (string))
						il.Emit (OpCodes.Call, convertsarray);
					else
						il.Emit (OpCodes.Call, convertarray.MakeGenericMethod (parameter.ParameterType.GetElementType ()));
					il.Emit (OpCodes.Br, done);
					il.MarkLabel (nullout);
					il.Emit (OpCodes.Ldnull);
					il.MarkLabel (done);
					il.Emit (OpCodes.Stloc, j+locoffset);
					j++;
				} else if (parameter.ParameterType == typeof (string)) {
					var nullout = il.DefineLabel ();
					var done = il.DefineLabel ();
					il.Emit (OpCodes.Ldarg, i);
					il.Emit (OpCodes.Brfalse, nullout);
					il.Emit (OpCodes.Ldarg, i);
					il.Emit (OpCodes.Call, convertstring);
					il.Emit (OpCodes.Br, done);
					il.MarkLabel (nullout);
					il.Emit (OpCodes.Ldnull);
					il.MarkLabel (done);
					il.Emit (OpCodes.Stloc, j+locoffset);
					j++;
				} else {
					var blockProxyAttribute = parameter.GetCustomAttribute<BlockProxyAttribute>();
					if (blockProxyAttribute != null)
					{
						var createDelegate = blockProxyAttribute.Type.GetMethod("CreateDelegate", BindingFlags.Static | BindingFlags.Public);
						il.Emit (OpCodes.Ldarg, i);
						il.Emit (OpCodes.Call, createDelegate);
						il.Emit (OpCodes.Stloc, j+locoffset);
						j++;
					}
				}

			}
#endif
		}

		protected void LoadArguments (ILGenerator il, int locoffset) {
			for (int i = ArgumentOffset, j = 0; i < ParameterTypes.Length; i++) {
				var parameter = Parameters[i - ArgumentOffset];
				if (parameter.ParameterType.IsByRef && IsWrappedType (parameter.ParameterType.GetElementType ())) {
					il.Emit (OpCodes.Ldloca_S, j+locoffset);
					j++;
				} else if (parameter.ParameterType.IsArray && IsWrappedType (parameter.ParameterType.GetElementType ())) {
					il.Emit (OpCodes.Ldloc, j+locoffset);
					j++;
				} else if (typeof (INativeObject).IsAssignableFrom (parameter.ParameterType) && !IsWrappedType (parameter.ParameterType)) {
					il.Emit (OpCodes.Ldarg, i);
					il.Emit (OpCodes.Newobj, parameter.ParameterType.GetConstructor (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new Type [] { typeof (IntPtr) }, null));
				} else if (parameter.ParameterType == typeof (string) || parameter.GetCustomAttribute<BlockProxyAttribute>() != null) {
					il.Emit (OpCodes.Ldloc, j+locoffset);
					j++;
				} else {
					il.Emit (OpCodes.Ldarg, i);
				}
			}
		}

		protected void UpdateByRefArguments (ILGenerator il, int locoffset) {
#if !MONOMAC_BOOTSTRAP
			// Keep in sync with DeclareLocals()
			for (int i = ArgumentOffset, j = 0; i < ParameterTypes.Length; i++) {
				var parameter = Parameters[i - ArgumentOffset];
				if (parameter.ParameterType.IsByRef && IsWrappedType (parameter.ParameterType.GetElementType ())) {
					Label nullout = il.DefineLabel ();
					Label done = il.DefineLabel ();
					il.Emit (OpCodes.Ldloc, j+locoffset);
					il.Emit (OpCodes.Brfalse, nullout);
					il.Emit (OpCodes.Ldarg, i);
					il.Emit (OpCodes.Ldloc, j+locoffset);
					il.Emit (OpCodes.Call, gethandle);
					il.Emit (OpCodes.Stind_I);
					il.Emit (OpCodes.Br, done);
					il.MarkLabel (nullout);
					il.Emit (OpCodes.Ldarg, i);
					il.Emit (OpCodes.Ldsfld, intptrzero);
					il.Emit (OpCodes.Stind_I);
					il.MarkLabel (done);
					j++;
				} else if (parameter.ParameterType.IsArray && IsWrappedType (parameter.ParameterType.GetElementType ())) {
					j++;
				} else if (parameter.ParameterType == typeof (string) || parameter.GetCustomAttribute<BlockProxyAttribute>() != null) {
					j++;
				}
			}
#endif
		}
	}
}
