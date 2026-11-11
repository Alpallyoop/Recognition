@echo off
REM Native-messaging host wrapper for Recognition's governed agent bridge (proposal 0012, Phase 4).
REM A browser launches THIS file (named in the native-messaging manifest) and pipes JSON over stdin/stdout.
REM It just starts recognition.exe in host mode, which bridges to the already-running Recognition instance's
REM loopback MCP endpoint (it reads the DPAPI-protected endpoint file the running instance writes while agent
REM control is on). No token is ever placed here or on the command line.
REM
REM Edit the path below to point at your built/installed recognition.exe, then register this .cmd in the
REM manifest (see docs/AGENT_NATIVE_HOST.md).
"%~dp0..\..\dist\recognition.exe" --agent-native-host
