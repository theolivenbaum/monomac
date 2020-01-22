//
// NSToolbarItem.cs: Support for the NSToolbarItem class
//
// Author:
//   Johan Hammar
//
using System;
using ObjCRuntime;
using Foundation;

namespace AppKit {

	public partial class NSToolbarItem {
		
		public event EventHandler Activated {
			add {
				Target = ActionDispatcher.SetupAction (Target, value);
				Action = ActionDispatcher.Action;
			}

			remove {
				ActionDispatcher.RemoveAction (Target, value);
			}
		}

	}
}
