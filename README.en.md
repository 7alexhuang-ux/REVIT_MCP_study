# Revit MCP - AI-Powered Revit Control

<div align="right">

[English](README.en.md) | [繁體中文](README.md)

</div>

<p align="center">
  <img src="https://img.shields.io/badge/Revit-2022-blue" alt="Revit 2022">
  <img src="https://img.shields.io/badge/Node.js-LTS-green" alt="Node.js">
  <img src="https://img.shields.io/badge/.NET-4.8-purple" alt=".NET 4.8">
  <img src="https://img.shields.io/badge/MCP-1.0-orange" alt="MCP Protocol">
</p>

Enable AI language models to directly control Autodesk Revit via Model Context Protocol (MCP), achieving AI-driven BIM workflows.

## 🎯 Key Features

- **Direct AI Control of Revit** - Operate Revit through natural language commands
- **Multi-Platform AI Support** - Claude Desktop, Gemini CLI, VS Code Copilot, Google Antigravity
- **Rich Revit Tools** - Create walls, floors, doors, windows, query elements, and more
- **Real-time Bidirectional Communication** - WebSocket real-time connection

## 📁 Project Structure

```
REVIT-MCP/
├── MCP/                    # Revit Add-in (C#)
│   └── MCP/
│       ├── Application.cs           # Main entry point
│       ├── ConnectCommand.cs        # Connection command
│       ├── RevitMCP.addin           # Add-in configuration
│       ├── Core/                    # Core functionality
│       │   ├── SocketService.cs     # WebSocket service
│       │   ├── CommandExecutor.cs   # Command executor
│       │   └── ExternalEventManager.cs
│       ├── Models/                  # Data models
│       └── Configuration/           # Configuration management
├── MCP-Server/             # MCP Server (Node.js/TypeScript)
│   ├── src/
│   │   ├── index.ts                 # MCP Server main program
│   │   ├── socket.ts                # Socket client
│   │   └── tools/
│       └── revit-tools.ts       # Revit tool definitions
│   ├── build/                       # Build output
│   ├── package.json
│   └── tsconfig.json
└── README.md
```

## 🔧 System Requirements

| Item | Requirement |
|------|------|
| **Operating System** | Windows 10 or later |
| **Revit** | Autodesk Revit 2022 / 2023 / 2024 |
| **.NET** | .NET Framework 4.8 |
| **Node.js** | LTS version (20.x or later) |

> 💡 **Important Note**: This tutorial uses Revit 2022 as an example, but applies to versions 2022, 2023, and 2024.  
> When installing, adjust the folder names according to your Revit version (see version mapping table in each step below).

## 📦 Installation Steps

#### Step 1: Install Revit Add-in (Just Copy Files)

**In simple terms: We need to put a file into Revit's specific folder.**

⚠️ **Important: Before you start, please confirm your Revit version**  
- Open Revit
- Click "Autodesk Revit 202X" in the top left (X is your version number)
- Then click "Help" → "About Autodesk Revit"
- Check the version number and remember it (for example: 2022, 2023, or 2024)

#### Method A: Use Pre-Built Version (Easiest! Recommended for Beginners)

If you don't want to build the program yourself, you can download the files we've already prepared:

##### Option 1: One-Click Install (Most Recommended!)

**The easiest way: Run the automatic installation script**

1. **Download the installation script**
   - Go to the `scripts/` folder in this project
   - Download `install-addon.bat` (Windows automatic installer)
   - Or download `install-addon.ps1` (PowerShell version)

2. **Run the installation**
   - **Method A**: Double-click the `install-addon.bat` file
     - Will automatically download files and place them in the Revit folder
     - Automatically detects your Revit version (2022, 2023, 2024)
   
   - **Method B**: Run with PowerShell (right-click and run as administrator)
     ```powershell
     powershell -ExecutionPolicy Bypass -File install-addon.ps1
     ```

3. **Done!**
   - When you see the "Installation successful" message, it's complete
   - Restart Revit

> 💡 **Advantages of automatic installation script**:
> - Automatically detects Revit version, no need to manually modify paths
> - Automatically downloads the latest RevitMCP.dll and RevitMCP.addin
> - Automatically copies to the correct folder
> - No need to understand command line

##### Option 2: Manual Installation

If you don't want to use the script, you can manually copy files:

1. **Download pre-built files**
   - Go to the `Releases` page of this project
   - Click on the latest version and download "RevitMCP.dll" and "RevitMCP.addin"
   - Download these 2 files to your desktop

2. **Put files in Revit's folder**
   - Press `Windows Key + R`, a "Run" window will appear
   - Copy and paste the path below, press Enter (**Note: Change 2022 to your Revit version**):
     ```
     %APPDATA%\Autodesk\Revit\Addins\2022
     ```
     
   > 💡 **Version Mapping Table**:
   > - Revit 2022: `Addins\2022`
   > - Revit 2023: `Addins\2023`
   > - Revit 2024: `Addins\2024`
   > 
   > Not sure about your Revit version? Open Revit → Help → About Revit, you'll see the version number

   - The Revit Add-in folder will open automatically
   - Place the 2 files you just downloaded there

3. **Restart Revit**
   - If Revit is running, close it completely
   - Then reopen Revit

#### Method B: Build the Program Yourself (For Developers)

If you understand code or want to learn how to build:

1. **Confirm .NET SDK is installed**
   - Open Command Prompt (press `Win + R`, type `cmd`)
   - Type: `dotnet --version`
   - If you see a version number (like 7.0.0), it's installed
   - If you see "command not found", download .NET SDK first: https://dotnet.microsoft.com/download

2. **Build the program**
   - Open Command Prompt
   - Type the following command:
     ```
     cd C:\Users\YourUsername\Desktop\MCP\REVIT_MCP_study\MCP\MCP
     dotnet build -c Release
     ```
   - Wait for the build to complete (you'll see green "Build succeeded" message)

3. **Copy the built files to Revit's folder**
   - After building, files will be in `bin\Release` folder
   - Press `Windows Key + R`, type (**Note: Change 2022 to your Revit version**):
     ```
     %APPDATA%\Autodesk\Revit\Addins\2022
     ```
     
   > 💡 **Version Mapping Table**:
   > - Revit 2022: `Addins\2022`
   > - Revit 2023: `Addins\2023`
   > - Revit 2024: `Addins\2024`

   - Copy all `.dll` and `.addin` files from `bin\Release` folder there

4. **Restart Revit**

### Step 2: Install MCP Server (The "Translator" Between AI and Revit)

**In simple terms: We need to install some software tools so AI can communicate with Revit.**

#### Pre-setup: Check if Node.js is Already Installed

MCP Server needs Node.js to run. First, check if it's installed:

1. **Open Command Prompt**
   - Press `Win + R`
   - Type `cmd`, press Enter

2. **Check Node.js**
   - In Command Prompt, type: `node --version`
   - If you see a version number (like v20.0.0), it's installed, **skip the download step**
   - If you see "command not found", it's not installed, follow the steps below to download

3. **Download and Install Node.js** (if needed)
   - Open your browser, visit https://nodejs.org
   - Click the "LTS" button on the left (recommended version)
   - Download the Windows installer (`.msi` file)
   - Run the downloaded installer and click "Next" until finished
   - Restart your computer

#### Installation Steps

1. **Open Command Prompt**
   - Press `Win + R`
   - Type `cmd`, press Enter

2. **Enter MCP Server folder**
   - Copy and paste the command below, press Enter (**Note: Change the username in the path to your account name**):
     ```
     cd C:\Users\YourUsername\Desktop\MCP\REVIT_MCP_study\MCP-Server
     ```
   - Hint: "YourUsername" is the account name you use to log into Windows
   
   > 💡 **Path different?**
   > - If you put the project folder in a different location (like C:\MCP), adjust the path accordingly
   > - To find your project folder: Right-click the MCP-Server folder → Properties → Location, copy that path

3. **Install software dependencies**
   - In Command Prompt, type:
     ```
     npm install
     ```
   - This will automatically download and install required software
   - Wait for completion (may take 1-5 minutes)
   - You should see "added XXX packages" when done

4. **Build the program (Convert to executable)**
   - Type the following command:
     ```
     npm run build
     ```
   - Wait for completion
   - You should see a `build/` folder created

**Congratulations! You've completed the installation.** Now you can proceed to the next step of configuration.

### Step 3: Configure AI Platform

Please refer to the **[Multi-Platform AI Agent Configuration](#-multi-platform-ai-agent-configuration)** section below.

---

## 🚀 How to Start

### 1️⃣ Launch Revit and Enable MCP Service

1. Open Revit 2022
2. Load or create a project
3. In the "MCP Tools" panel, click the "**MCP Service (On/Off)**" button
4. Confirm you see "WebSocket Server Started, Listening: localhost:8765"

> 💡 **About Port Numbers**:
> - `8765` is the default port for MCP Server
> - Port numbers are arbitrary and can be occupied by other programs
> - If you see "Port 8765 is in use" error, you need to manually adjust:
>   1. Open the configuration file `MCP-Server/src/index.ts`
>   2. Find the line with `PORT = 8765`
>   3. Change to another unused port, like `8766` or `9000`
>   4. Recompile: `npm run build`
>   5. Update the port number in all AI applications that use this MCP Server (to the same new port)

### 2️⃣ Connect via AI Platform

Depending on your chosen AI platform, refer to the setup instructions below.

---

## 🤖 Multi-Platform AI Agent Configuration

### Core Concept: MCP Clients and MCP Server

Before you start, you need to understand the core concepts of this architecture:

#### What is MCP Client?

**MCP Client** refers to AI applications that understand and use MCP tools. In simple terms:
- Claude Desktop
- Gemini CLI
- VS Code Copilot
- Google Antigravity

These applications have built-in "MCP Client" functionality, allowing them to read and call tools provided by the MCP Server.

#### What is MCP Server?

**MCP Server** is the Node.js application in this project (`MCP-Server/build/index.js`), which:
- Defines Revit operation tools (create_wall, query_elements, etc.)
- Communicates with Revit Add-in via WebSocket
- Converts AI commands to Revit API calls

---

### 4+1 Solution Architecture

This project provides **5 usage solutions**, divided into two categories:

#### External Invocation Solutions (4 types)

These solutions follow the same architecture:

```
┌─────────────────┐
│  AI Application │  (Claude Desktop / Gemini CLI / VS Code / Antigravity)
│  (MCP Client)   │
└────────┬────────┘
         │ 1. Read MCP Server address
         │
┌────────▼────────┐
│   MCP Server    │  (Node.js - This project)
│  (Revit Tools)  │
└────────┬────────┘
         │ 2. WebSocket connection
         │
┌────────▼────────┐
│  Revit Add-in   │  (C# - RevitMCP.dll)
│  (WebSocket)    │
└────────┬────────┘
         │ 3. Revit API calls
         │
┌────────▼────────┐
│ Revit Application│
└─────────────────┘
```

**Features:**
- AI applications have built-in MCP support, no API Key needed
- MCP Server only handles tool definitions and communication
- All API Keys are managed by the AI application itself (like Claude Desktop has its own API Key)

---

#### Embedded Solution (1 type)

```
┌────────────────────────────────┐
│   Revit Application            │
├────────────────────────────────┤
│  Revit Add-in with AI Chat     │
│                                │
│  ┌──────────────────────────┐  │
│  │  Chat Window UI (WPF)    │  │
│  └──────────────────────────┘  │
│           │ Use API Key         │
│  ┌────────▼──────────────────┐  │
│  │  GeminiChatService        │  │
│  │  (C# calls Gemini)        │  │
│  └────────┬──────────────────┘  │
│           │                     │
└───────────┼─────────────────────┘
            │ HTTP request to Gemini API
            │
        ┌───▼──────┐
        │ Gemini   │
        │ API      │
        └──────────┘
```

**Features:**
- Runs completely inside Revit, no need to launch external applications
- Directly calls Gemini API, requires API Key
- Smoothest user experience (chat directly inside Revit)

---

### Why Only the Embedded Solution Needs API Key?

This is the key difference:

| Solution | Need API Key | Reason |
|------|------|------|
| Claude Desktop | ❌ No | Claude Desktop is bound to your Anthropic account and API Key |
| Gemini CLI | ❌ No | Gemini CLI is bound to your Google account |
| VS Code Copilot | ❌ No | GitHub Copilot is bound to your GitHub account and authorization |
| Antigravity | ❌ No | Antigravity is bound to your Google Cloud account |
| **Embedded Chat (Gemini API)** | **✅ Yes** | This **directly** calls Gemini API, not through an application intermediary |

In simple terms:
- **External 4 solutions**: AI application is already a "paying customer", you just use it directly
- **Embedded solution**: You yourself become a "paying customer" of Gemini API, need to provide API Key

---

### MCP Server's Role in Each Solution

No matter which solution you use, **MCP Server's role is the same**:

```
MCP Server's Responsibilities:
1. Define Revit tools (create_wall, query_elements, etc.)
2. Receive tool invocation requests from AI applications
3. Forward requests to Revit Add-in via WebSocket
4. Return execution results to AI applications
```

MCP Server **does not directly** communicate with any AI API, it's just a "translator".

---

### Solution Selection Guide

| Scenario | Recommended Solution | Reason |
|------|------|------|
| Daily use, simplest | Claude Desktop | No additional configuration, use pre-built app directly |
| Want to chat in Revit | Embedded Chat (Gemini API) | Smoothest user experience |
| Prefer Google | Gemini CLI | Use your own Google account |
| Software developers | VS Code Copilot | Seamless use in development environment |
| Advanced AI development | Antigravity | Multi-window and async agent execution |

---

### Solution 1️⃣: Gemini CLI

Gemini CLI is Google's command-line AI tool that lets you chat directly with Gemini 2.5 Flash in the terminal.

#### Step 1: Install Gemini CLI (Beginner-Friendly)

**What is Gemini CLI?** It's a tool that can run in Windows Command Prompt or PowerShell.

1. **Download Node.js** (if not already installed)
   - Go to https://nodejs.org
   - Click the "LTS" version download
   - Run the downloaded installer and click "Next" all the way to completion
   - Restart your computer

2. **Open PowerShell**
   - Press `Win + X`
   - Select "Windows PowerShell (Administrator)"
   - Copy and paste the command below, press Enter:
   ```powershell
   npm install -g @google/gemini-cli
   ```
   - Wait for installation to complete (you'll see a green checkmark)

#### Step 2.5: Use Pre-Configured Version from This Project (Easiest!)

**Easiest way: Directly use the configuration files we've prepared**

1. **Find the project's configuration file**
   - In the `MCP-Server` folder, find `gemini_mcp_config.json`
   - Remember its full location (example: `C:\Users\User\Desktop\REVIT MCP\MCP-Server\gemini_mcp_config.json`)

2. **Run in PowerShell**
   ```powershell
   $env:PATH = "C:\Program Files\Git\bin;$env:PATH"
   cd "C:\Users\User\Desktop\REVIT MCP\MCP-Server"
   gemini --config gemini_mcp_config.json
   ```

#### Step 3: Launch (Beginner Version)

1. **First launch Revit**
   - Open Revit 2022
   - In the "MCP Tools" panel, click "**MCP Service (On/Off)**" button
   - You'll see "WebSocket server started" when successful

2. **Then open Gemini CLI**
   - Open PowerShell (Administrator)
   - Run:
   ```powershell
   gemini
   ```
   - Now you can type commands in Gemini to control Revit

3. **Test conversation**
   ```
   > List all floors in the Revit project
   > Create a 5-meter-long wall for me
   ```

---

### Solution 2️⃣: VS Code (GitHub Copilot)

Chat directly with AI and control Revit in the VS Code code editor.

#### Step 1: Install VS Code

1. Go to https://code.visualstudio.com
2. Click the blue "Download" button
3. Run the downloaded installer
4. Click "Next" all the way to completion, restart your computer

#### Step 2.5: Use Pre-Configured Version from This Project (Recommended!)

**Good news: We've prepared the configuration file for you!**

1. **Open this project's folder**
   - Right-click the `c:\Users\User\Desktop\REVIT MCP` folder
   - Select "**Open with VS Code**"
   - Or in VS Code, click File → Open Folder, select this folder

2. **Configuration file is in `.vscode/mcp.json`**
   - The file is ready, you don't need to modify anything
   - The system will load this configuration automatically

#### Step 3: Launch (Beginner Version)

1. **Confirm Revit MCP service is running**
   - Open Revit 2022
   - Click "MCP Service (On/Off)"

2. **Open Copilot Chat in VS Code**
   - Press `Ctrl + Shift + I`
   - Or click the Copilot icon on the left
   
3. **Start asking**
   - In the chat box, type: "Find all columns in the Revit project"
   - AI will automatically execute Revit tools for you

---

### Solution 3️⃣: Claude Desktop (Recommended for Beginners!)

Anthropic's official desktop application, this is the **simplest way**.

#### Step 1: Install Claude Desktop

1. Go to https://claude.ai/download
2. Click "Download for Windows"
3. Run the downloaded `.exe` installer
4. After installation, restart your computer

#### Step 2.5: Add MCP Directly in Claude Desktop (Simplest!)

**No file copying needed! Configure directly in the application:**

1. **Open Claude Desktop application**

2. **Click the "⚙️ Settings" icon in the top right**
   - Or find "Settings" in the bottom left

3. **Find the "MCP Servers" option**

4. **Click "Add Server" or "New Server"**

5. **Fill in the following information**
   - **Name**: `revit-mcp`
   - **Command**: `node`
   - **Arguments**: `C:\Users\User\Desktop\REVIT MCP\MCP-Server\build\index.js`
   - **Environment Variables**:
     ```
     REVIT_VERSION: 2022
     ```
     
   > 💡 **Different version? Modify the environment variable**:
   > - Revit 2022: Change to `REVIT_VERSION: 2022`
   > - Revit 2023: Change to `REVIT_VERSION: 2023`
   > - Revit 2024: Change to `REVIT_VERSION: 2024`

6. **Click "Save"** - Done!

#### Step 3: Launch (Beginner Version)

1. **Launch Revit**
   - Open Revit 2022
   - Click "MCP Service (On/Off)"

2. **Use Claude Desktop**
   - Claude application will connect to Revit automatically
   - Type a conversation in the chat box, example:
   ```
   Create a 3m × 5m floor in Revit for me
   ```

3. **Claude will automatically execute the operation for you!**

---

### Solution 4️⃣: Google Antigravity

[Google Antigravity](https://antigravity.google/) is Google's "agent-first" development platform that brings IDE into the AI Agent era.

**Main Features:**
- Based on open-source VS Code, but radically changes the user experience
- Interface split into two main windows: **Editor** and **Agent Manager**
- Can **dispatch multiple agents** to handle different tasks simultaneously (non-linear, asynchronous execution)
- Built-in **Antigravity Browser** (browser sub-agent) for web testing and screen recording
- Agents generate "Artifacts" such as task plans, implementation plans, code diffs, screenshots, etc.
- Currently only available as a preview version for **personal Gmail accounts** (free to use)

#### Step 1: Install Google Antigravity

1. **Go to download page**
   - Open your browser, go to https://antigravity.google/download
   - Click the version for your operating system (Windows / Mac / Linux)
   - Run the installer and complete installation

2. **Launch Antigravity and complete setup**
   - Open the Antigravity application
   - Choose setup flow (can import from existing VS Code or Cursor settings, or start fresh)
   - Choose editor theme (dark/light)
   - Choose agent usage mode:
     - **Agent-Directed Development**: Agent operates autonomously, minimal human intervention
     - **Agent-Assisted Development** (recommended): Agent makes decisions and returns them for user approval
     - **Review-Driven Development**: Agent always requests review
     - **Custom Settings**: Fully customizable control

3. **Sign in with Google account**
   - Click "Sign in to Google"
   - Sign in with your personal Gmail account
   - System will create a new Chrome profile for this

#### Step 2: Configure Browser Agent (Antigravity Browser)

A major feature of Antigravity is the built-in browser sub-agent that lets AI directly operate web pages.

1. **Start conversation in Agent Manager**
   - Select `Playground` or any workspace
   - Type a command that needs browser (example: "Go to antigravity.google")

2. **Install Chrome Extension**
   - Agent will prompt to set up browser agent
   - Click `Setup`, follow instructions to install Chrome extension
   - After installation, agent can control browser to execute tasks

#### Step 3: Configure MCP Server to Connect to Revit

> ⚠️ **Note**: Antigravity runs locally, MCP Server also needs to run on the same Windows computer (because it needs to connect to Revit).

1. **Open workspace**
   - In Agent Manager, click `Workspaces`
   - Select this project's `MCP-Server` folder as the workspace

2. **Launch MCP connection via conversation**
   - In Agent Manager, start a new conversation
   - Tell Agent: "Please run node build/index.js to start MCP Server"
   - Or directly in the editor's terminal, run:
     ```
     cd C:\Users\YourUsername\Desktop\REVIT MCP\MCP-Server
     node build/index.js
     ```

3. **Start interacting with Revit**
   - Confirm Revit is launched and MCP service is on
   - In Agent Manager, type a command, example:
     ```
     Create a 5-meter-long wall in Revit for me
     ```

#### 🎯 Antigravity's Unique Advantages

| Feature | Description |
|------|------|
| **Multi-Agent Parallel Execution** | Can dispatch 5+ agents to handle different tasks simultaneously |
| **Artifacts** | Agents generate task plans, implementation plans, code diffs, screenshots, browser recordings, etc. |
| **Browser Integration** | Built-in Chrome browser sub-agent with click, scroll, input, read console capabilities |
| **Inbox** | Centrally track all conversations and task status |
| **Google Docs-style Comments** | Add comments to artifacts and code diffs, agents iterate based on feedback |

> 📚 **More Information**: See [Google Antigravity Official Tutorial](https://codelabs.developers.google.com/getting-started-google-antigravity?hl=en)

---

## 🛠️ Available MCP Tools

| Tool Name | Description |
|---------|------|
| `create_wall` | Create wall |
| `create_floor` | Create floor |
| `create_door` | Create door |
| `create_window` | Create window |
| `get_project_info` | Get project information |
| `query_elements` | Query elements |
| `get_element_info` | Get element details |
| `modify_element_parameter` | Modify element parameters |
| `delete_element` | Delete element |
| `get_all_levels` | Get all levels |

---

## 🚀 Advanced Feature: Integrate AI API in Revit Add-in (Gemini 2.5 Flash)

*For brevity, the advanced feature section is identical to the Chinese version. Please refer to the Chinese README for the complete implementation details.*

---

## 🔒 Security Considerations

⚠️ **Important Security Reminders**:

1. **Port Management** - MCP Server listens on `localhost:8765` by default, access limited to this machine only
2. **Firewall** - Not recommended to open ports to external networks
3. **Code Review** - Confirm code source is trustworthy before running
4. **Backup** - Backup your Revit project before operating
5. **API Key Protection** - Never submit API Keys to GitHub, use environment variables to manage

## 📝 FAQ

### Q: Revit doesn't show MCP Tools panel?
A: Confirm `RevitMCP.addin` is correctly placed in the Add-in directory and restart Revit.

### Q: MCP Server can't connect to Revit?
A: 
1. Confirm you clicked "MCP Service (On/Off)" in Revit to start the service
2. Confirm Port 8765 is not occupied by other programs
3. Check firewall settings

### Q: AI says it can't find Revit tools?
A: Confirm MCP Server configuration file path is correct and restart the AI application.

---

## 📖 Appendix: Technical Supplementary Notes

> 💡 The content below is advanced technical information. General users can skip this section.

### A. What is WebSocket?

This project uses **WebSocket** as the communication protocol between MCP Server and Revit Add-in.

**WebSocket** is a networking communication standard (not created by this project) with the following characteristics:

| Feature | Description |
|------|------|
| **Bidirectional Communication** | Server and client can send messages to each other anytime |
| **Low Latency** | Connection stays open, no need to reconnect each time |
| **Real-time** | Suitable for operations needing quick response (like real-time Revit control) |

**Simple Analogy:**
- Traditional HTTP = Call phone each time, hang up after talking
- WebSocket = Keep call connected, both can talk anytime

### B. Why Choose WebSocket?

Reasons for this project choosing WebSocket:

1. **Real-time Requirement** - Revit operations need immediate response
2. **Persistent Connection** - Multiple AI commands are sent continuously, single connection is more efficient
3. **Bidirectional Communication** - Revit sometimes needs to actively notify (like progress updates, error messages)
4. **Cross-Language Support** - Both Node.js and C# have native support
5. **MCP Standard** - Model Context Protocol officially uses WebSocket

### C. Comparison of Other Communication Technologies

If you're interested in other technical options:

| Technology | Latency | Bidirectional | Ease of Use | Use Case |
|------|------|------|--------|----------|
| **WebSocket** ✅ | Low | ✅ | ⭐⭐⭐⭐ | This project uses it |
| HTTP REST | High | ❌ | ⭐⭐⭐⭐⭐ | Simple queries |
| gRPC | Lowest | ✅ | ⭐⭐ | High-performance scenarios |
| Named Pipes | Lowest | ✅ | ⭐⭐ | Local-only communication |
| SignalR | Low | ✅ | ⭐⭐⭐⭐ | .NET ecosystem |

### D. Port Number (Port) Supplementary Explanation

This project uses `8765` as the default port, which is an arbitrary choice.

**Common port ranges:**
- `0-1023`: System reserved ports (like 80=HTTP, 443=HTTPS)
- `1024-49151`: Registered ports (used by common applications)
- `49152-65535`: Dynamic/private ports (can be freely used)

`8765` is in the registered port range, usually won't conflict with system services, but can be occupied by other applications.

---

## 📄 License

MIT License

## 🤝 Contributing

Welcome to submit Issues and Pull Requests!

---

**Enjoy your AI-powered Revit development! 🚀**
