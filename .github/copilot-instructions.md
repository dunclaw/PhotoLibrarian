\# Copilot Instructions



\## App preferences

A fast, strictly local (no cloud service) photo library manager for Windows — built with WinUI 3 and Win2D.

This app is designed for average users to manage photos. No complicated external dependencies on separately installed command line tools or obscure settings should be added. Design for the user in mind. No python dependency should be used - this must be a self-contained local install only. Data should all be processed locally and stored with the photo itself where possible - no cloud service dependency or secret database where critical information resides. It's OK to use a db for quick access and indexing, but photo libraries should be portable to another computer just by moving the photos. Tagging and metadata should be preserved. Extreme care should be taken to not do anything potentially destructive to a user's photos without explicit instructions and backups should be made where feasible for recovery.

The app is primarily in C# but native C++ dependencies and code are acceptable if necessary for performance.



\## C# Style Preferences



\- All C# code files must begin with `// Copyright (c) Duncan Lawler. All Rights Reserved.`

\- Use good C# style with appropriate commenting and readability. 

\- Avoid long obtuse and hard to read constructs like multiple nested lambda statements together on a single line

\- be aware of debuggability when using async, threading, and anonymous lambda constructs that make debugging and decoding call stacks difficult to follow



