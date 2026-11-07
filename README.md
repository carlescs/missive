# Missive

Missive is a lightweight, cross-platform desktop HTTP client — a simplified take
on Postman. Its difference: your collections are plain files in a folder you
choose, and Missive can synchronize that folder with git directly from the app.
No account, no cloud workspace, no proprietary export format.

> **Status:** early development. You can open a workspace folder, browse its
> request files in the explorer (a sidebar you can resize by dragging its edge),
> edit them in the tabbed request editor and save changes or new requests back
> to disk, send them with `{{variable}}` values from the selected environment
> and inspect the response, and sync the folder with git from the Source
> control view (status, commit, pull, push).

## Install

Download the file for your system from the
[latest release](https://github.com/carlescs/missive/releases/latest):

| System | File | How to install |
|---|---|---|
| Windows (x64) | `Missive-win-Setup.exe` | Run it. Missive installs for your user (no admin rights) and adds Start menu and desktop shortcuts; uninstall it from *Installed apps*. |
| macOS (Apple silicon) | `Missive-osx-Setup.pkg` | Open it and follow the installer. |
| Linux (x64) | `Missive.AppImage` | Make it executable (`chmod +x Missive.AppImage`) and run it; keep it wherever you like. |

Missive checks for a new version at startup and downloads it in the background;
when one is ready, a **Restart to update** button appears in the header. Nothing
is installed until you click it. Builds run from source don't update
themselves.

Missive needs [git](https://git-scm.com/downloads) for workspace sync, plus the
system webview: Windows 10 and 11 normally include the Microsoft Edge WebView2
Runtime, macOS includes WebKit, and Linux needs GTK 3, WebKitGTK 4.1 and
libnotify (on Ubuntu 24.04:
`sudo apt-get install libgtk-3-0t64 libwebkit2gtk-4.1-0 libnotify4`).

The builds are not code-signed yet, so Windows SmartScreen may warn about an
unrecognized app (*More info* → *Run anyway*), and macOS may refuse to open it
until you allow it in *System Settings* → *Privacy & Security*.

## Why Missive

- **Files first.** A collection is just a folder. Each request is one readable
  YAML file you can open, grep, review and edit with any tool.
- **Git native.** Share and version collections the same way you share code:
  commit, pull and push from inside Missive, using your existing git setup.
- **Local and private.** Secrets and response history stay on your machine and
  never end up in the repository.

## Planned features

- [x] Open a folder as a workspace and browse its requests
- [x] Create, rename, duplicate and delete (nested) collection folders, and move
      requests and folders between them with drag and drop
- [x] Duplicate requests, and reopen recently used workspaces
- [x] Request descriptions in Markdown (a **Docs** tab), imported and exported
- [x] Request editor: method, URL, query parameters, headers, auth and body
      (JSON, raw text, form-data, `x-www-form-urlencoded`), saved as YAML files
- [x] Sending requests
- [x] Response viewer: status, time, size, headers and a pretty-printed body,
      with HTML and PDF previews; copy or save the body, cookies set, a timing
      breakdown with redirects, and the request as sent
- [x] Optional cookie jar per environment (kept out of git)
- [x] Capture values from responses into variables (JSONPath or header, kept
      out of git)
- [ ] Collections as folders, with optional folder-level defaults
- [x] Environments with `{{variable}}` substitution
- [x] Auth helpers: Basic, Bearer token and API key
- [ ] Local response history (kept out of git)
- [ ] Git sync: changed-file status and diffs, commit, pull, push, branches
      (switch, create, check out, delete), remotes, and clear reporting of
      merge conflicts
- [x] Import from Postman, Insomnia, HAR and OpenAPI/Swagger; export for
      Postman and Insomnia

### Non-goals

Missive deliberately stays small. Out of scope for now: pre-request/test
scripting, collection runners, mock servers, cloud accounts and hosted team
workspaces (git is the collaboration layer).

## Workspace format

A workspace is a folder, usually a git repository:

```text
my-api/
├── missive.yaml            # workspace metadata
├── environments/
│   ├── dev.yaml
│   └── prod.yaml
├── users/                  # a folder is a collection
│   ├── _folder.yaml        # optional folder-level defaults (headers, auth)
│   ├── get-user.yaml
│   └── create-user.yaml
└── .missive/               # local only, git-ignored: secrets, cookies, captured values, history, UI state
```

A request file:

```yaml
name: Create user
method: POST
url: "{{baseUrl}}/users"
headers:
  Content-Type: application/json
auth:
  type: bearer
  token: "{{apiToken}}"
body:
  type: json
  content: |
    {
      "name": "Ada Lovelace"
    }
```

Keys are always written in this order — `name`, `method`, `url`, `params`,
`headers`, `auth`, `body`, `capture`, `description` — and empty sections are
left out. Query parameters
use the same `name: value` form as headers. A parameter or header that is
disabled in the editor is kept in the file with a `~` prefix (so names cannot
start with `~`):

```yaml
params:
  page: "1"
  ~debug: "true"     # disabled
```

Auth types are `basic` (`username`, `password`), `bearer` (`token`) and
`apiKey` (`key`, `value`, and `in: query` to send it as a query parameter
instead of a header). Body types are `json`, `text`, `form` and `urlencoded`.
`form` (multipart) and `urlencoded` bodies hold one `name=value` pair per line;
`urlencoded` values are sent as written, so percent-encode them yourself.

`capture` stores values from a successful response in variables, so a login
request can hand its token to the requests after it — declaratively, without
scripts:

```yaml
capture:
  token: $.access_token    # a JSONPath into the JSON body
  etag: header:ETag        # a response header
```

See [Capturing response values](#capturing-response-values).

`description` holds notes about the request in Markdown, edited in the request
editor's **Docs** tab (with an **Edit** and a **Preview** mode). It is written
last, as a block, so the docs live next to the request in git:

```yaml
description: |
  Creates a user. Needs the `admin` role.

  Returns `409` when the email is taken.
```

The preview renders GitHub-flavored Markdown but shows raw HTML as text and
images as their alt text, and links are not followed, so nothing is loaded from
the network.

New requests are saved as a file named after the request (`Create user` →
`create-user.yaml`, or `create-user-2.yaml` if that name is taken). Renaming a
request renames its file too, unless the file name was chosen by hand. The
explorer lists every `.yaml`/`.yml` file except `missive.yaml`, `_folder.yaml`,
the `environments/` folder and hidden files and folders; files that can't be
read are shown with an error instead of being hidden. Saving a request whose
content didn't change leaves its file untouched. Comments and keys Missive
doesn't know are not preserved when a request is saved with changes.

Folders are collections and can be nested. From the explorer you can create,
rename (F2) and delete them. Drag a request or a folder onto another folder
(or onto empty space in the explorer for the workspace root) to move it there;
moved files keep their names, and nothing is moved if the destination already
has an entry with that name. Open requests follow renamed and moved folders. Folder
names are kept as typed, but must work on Windows, macOS and Linux (no
`/ \ < > : " | ? *`, no leading dot or trailing dot, no reserved names such as
`CON`), and `environments` is reserved at the workspace root. Deleting a folder
removes the requests in it and only goes ahead when the folder holds nothing
else — request files, `_folder.yaml`, `.gitkeep` and OS files such as
`.DS_Store` — so other files you keep there are never deleted. Links, and
names Missive can't handle as workspace paths (such as `notes:2026.txt` or a
name with a backslash on Linux and macOS), also block the delete. Git doesn't
track empty folders, so a new folder appears in commits once it contains a
request.

Right-click a request or a folder in the explorer to duplicate it. A copied
request is named `… copy` (`Get user` → `Get user copy`) and saved next to the
original as `get-user-copy.yaml` (or `get-user-copy-2.yaml` if that is taken).
A copied folder is named `… copy` (then `… copy 2`, `… copy 3`, …) and holds a
byte-for-byte copy of everything in the original. A folder holding symbolic
links, or names that can't be used on every system (such as `a:b.txt` on
Linux), is not copied, so a copy is never silently incomplete. Duplicating only
writes new files and never changes existing ones.

An environment file (`environments/dev.yaml`):

```yaml
name: Development
cookieJar: true
variables:
  host: localhost:5000
  baseUrl: http://{{host}}
secrets:
  - apiToken
```

Keys are written in the order `name`, `cookieJar`, `variables`, `secrets`, and
empty sections and defaults (`cookieJar: false`) are left out. Variable names can't contain spaces or braces, and a
value can use other variables (`{{host}}` above); variables that refer to each
other in a loop are refused. `secrets` lists only the *names* of secret
variables: their values stay on each computer, in `.missive/secrets.yaml`, so
they are never committed. After cloning or pulling, each person enters their own
secret values in Missive:

```yaml
# .missive/secrets.yaml — local only
dev.yaml:
  apiToken: s3cr3t
```

`cookieJar: true` makes requests sent with the environment keep cookies between
them (see [Sending requests](#sending-requests)); the cookies themselves are
local too, in `.missive/cookies.json`:

```json
{
  "dev.yaml": [
    { "name": "session", "value": "abc123", "domain": "localhost", "path": "/", "httpOnly": true }
  ]
}
```

Values that requests capture from responses are local as well, in
`.missive/captures.yaml`, with the same layout as `secrets.yaml`.

A `domain` with a leading dot (`.example.com`) is a cookie set with a `Domain`
attribute, sent to that domain and its subdomains; one without (`localhost`
above) was set without one and is sent to that host only.

The selected environment is remembered per workspace in `.missive/state.yaml`.
Whenever Missive writes to `.missive/`, it makes sure the workspace's
`.gitignore` contains `.missive/` (creating the file if needed), and git
operations skip `.missive/` regardless. Files are written with stable key order
and minimal noise so diffs stay small and merges stay easy. The format is still
evolving.

## Environments

Pick the environment requests are sent with from the header, or from the
**Environments** view in the activity bar, which also lists every file in
`environments/` and creates new ones. Clicking an environment opens it in an
editor tab, next to the open requests: its name, and a table of variables, each
of which can be marked **Secret**. New environments are saved as a file named after them
(`Staging` → `staging.yaml`); renaming one renames its file too, unless the file
name was chosen by hand, and its secret values, cookies, captured values and
selection follow. Deleting an environment also removes its stored secret values,
cookies and captured values.

**Keep cookies between requests** turns on the environment's cookie jar (off by
default). Cookies that responses set, including redirects on the way, are then
kept and sent with later requests using the environment, following the usual
domain, path, expiry and `Secure` rules, so APIs that log in with a session
cookie work as in a browser. A `Cookie` header you set yourself is still sent,
with the jar's cookies added after it. The editor lists the kept cookies, where
each can be deleted, or all cleared at once; turning the jar off and saving
forgets them.

In the request editor, `{{variable}}` references are highlighted in the URL and
body: in violet when the selected environment defines them, in red when it
doesn't. Hover over one to see its value (secret values are not shown).

## Sending requests

**Send** (or Ctrl+Enter) sends the request as it is in the editor, saved or
not; **Cancel** stops it. Requests are sent by the .NET backend, so browser
rules such as CORS don't apply. When sending:

- Enabled query parameters are appended to the URL, which gets `http://` if it
  has no scheme. Disabled parameters and headers are left out.
- `{{variable}}` references in the URL, parameters, headers, auth and body are
  resolved first with the selected environment. A request that uses a variable
  the environment doesn't define (or a secret with no value on this computer)
  is not sent; the undefined names are listed with a hint on where to set them.
- Auth adds an `Authorization` header (Basic or Bearer) or the API key header or
  query parameter; a header you set yourself takes precedence. JSON, text and
  `urlencoded` bodies get a matching `Content-Type` unless you set one.
- A `User-Agent: Missive` header is added unless you set one. Compressed
  responses are decompressed. No cookies are kept between requests unless the
  environment's cookie jar is on. A request times out after 60 seconds.
- Up to 20 redirects are followed, never from `https` to `http`. A 303, or a
  301/302 answering a POST, continues as a GET without a body; 307 and 308 keep
  the method and body. The `Authorization` header is not sent on to the new
  location.

The response pane shows the status, the time until the whole body was read, the
body size and these tabs:

- **Body**, described below. **Copy** copies a text body as shown, and **Save**
  writes the body to a file exactly as it was received (the bytes, not the
  decoded text), suggesting a name from `Content-Disposition`, or from the URL
  and `Content-Type` (`/users` answered with JSON → `users.json`).
- **Headers**, in the order received.
- **Cookies**: the cookies the response (and any redirect before it) set, with
  their domain, path, expiry and flags.
- **Timing**: where the time went — DNS lookup, TCP connect, TLS handshake,
  waiting for the first byte, and download — and the redirects followed, each
  with its status, URL and `Location`. Connection phases are left out when no
  new connection was needed.
- **Request**: the request as it went on the wire after redirects: the resolved
  URL and the final headers, including those Missive adds (`User-Agent`,
  `Content-Type`, `Content-Length`, `Cookie`). Credentials (`Authorization`,
  the API key) and the values of secret variables are masked.

Request and response bodies are shown in the Monaco
editor with syntax highlighting (JSON, XML, HTML, JavaScript, CSS, YAML,
GraphQL and Markdown, chosen from the body type or the response's
`Content-Type`); JSON request bodies are also validated as you type. JSON
responses are pretty-printed (switch to **Raw** for the original text). HTML
responses are previewed rendered in a sandboxed frame that runs no scripts and
loads nothing from the network
(switch to **Source** for the markup), PDFs are rendered with pdf.js (fit to width
or zoomed, with selectable text), images are previewed, and other binary bodies
are not shown (use **Save** to get them). Bodies over 20 MB are cut off. Responses are kept in memory per open
request only; nothing is written to the workspace. The bodies of the latest
responses (up to 64 MB in all) are kept by the backend so they can be saved.

## Capturing response values

A request's **Capture** tab lists variables to fill from its response: each
row names a variable and where its value comes from, either a JSONPath into the
JSON body or a header:

| From | Captures |
| --- | --- |
| `$.access_token` | the `access_token` property of the body |
| `$.data.items[0].id`, `$['odd key']` | nested properties and array items |
| `$` | the whole body |
| `header:ETag` | the first `ETag` header (names are case-insensitive) |

Only that subset of JSONPath is supported — no wildcards, filters or recursive
descent — so a capture always names a single value. Strings are captured as
they are, numbers and booleans as written, and objects and arrays as compact
JSON; a `null` or missing value captures nothing.

After a successful (2xx) response, the captured values are stored for the
selected environment, on this computer only (`.missive/captures.yaml`), never in
tracked files. They take precedence over the environment's own values of the
same variables, so the next requests use `{{token}}` straight away; hovering a
reference says when its value was captured, and a captured value of a secret
variable is masked like any secret. Responses with another status capture
nothing, so an error never overwrites a good token, and without a selected
environment nothing is stored. The response's **Captures** tab shows what each
rule captured, or why it captured nothing. The environment editor lists the
captured values, where each can be forgotten (bringing back the environment's
value), or all at once.

Captures are not imported or exported: other tools express them as scripts.

## Import and export

Missive doesn't need its own exchange format — the workspace *is* plain files —
but you can bring requests in from other tools and take them out again.

**Import** (the import button in the explorer header, or on a folder row to
import into that folder) reads a file and recognizes its format from the
content, in JSON or YAML:

| Format | What is imported |
| --- | --- |
| Postman collection v2.0 / v2.1 | Folders, requests, auth (inherited from folders and the collection), bodies; collection variables become an environment. Path variables (`/users/:id`) become their value, or `{{id}}` if they have none. |
| Postman environment | An environment; `secret` variables stay secret (their values go to `.missive/secrets.yaml`). |
| Insomnia export v4 | Folders, requests and environments: each sub-environment becomes an environment that includes the base environment's variables. `{{ _.name }}` references become `{{name}}`. |
| HAR 1.2 (browser dev tools) | One request per entry, in a folder per host, without connection headers such as `Host`. |
| OpenAPI 3.x / Swagger 2.0 | One request per operation, in a folder per tag, with example values for parameters and an example JSON body built from the schema. Each server becomes an environment defining `{{baseUrl}}`; security schemes become Basic, Bearer or API key auth using secret `{{username}}`/`{{password}}`, `{{token}}` or `{{apiKey}}` variables, which you fill in. Path parameters without an example become `{{variables}}`. |

Request descriptions are imported from Postman and Insomnia, and from an
OpenAPI operation's `description` (its `summary` names the request).

Imported requests are saved as ordinary request files in a new folder named
after the collection (`Shop`, or `Shop 2` if that name is taken), and
environments as new environment files; nothing that already exists is changed.
What Missive can't represent is skipped and listed after the import: other auth
types (OAuth 2, digest, …), file uploads, scripts, and HTTP methods other than
`GET`, `POST`, `PUT`, `PATCH`, `DELETE`, `HEAD` and `OPTIONS`. Repeated query
parameters or headers keep their first value, and a GraphQL body becomes the
equivalent JSON body.

**Export** (the export button in the explorer header) writes the whole
workspace to a file you choose:

- **Postman collection v2.1** (`.postman_collection.json`), which Postman,
  Insomnia, Bruno, Hoppscotch and most other clients import. Each environment
  is written next to it as a Postman environment file
  (`dev.postman_environment.json`; a name that is taken gets a number).
- **Insomnia v4** (`.insomnia.json`), a single file with the requests and the
  environments, using Insomnia's `{{ _.name }}` references.

Request descriptions are exported too. Untick **Include environments** to
export only the requests. Secret values are
never exported: secret variables are exported with empty values (as `secret`
variables in Postman). Request files that can't be read are skipped and
reported. Exporting the same workspace again produces the same file, so
exports diff cleanly.

## Git sync

When the workspace folder is a git repository, Missive shows which request and
environment files changed, lets you commit them with a message, pulls and
pushes the current branch, and switches, creates and deletes branches. It uses your existing git credentials. Missive never
force-pushes and never resolves conflicts automatically: conflicting files are
reported so you can resolve them (in Missive's editor or your own tools).

Missive runs your installed `git` command-line client, so your git
configuration, hooks and credential helpers apply as usual. In detail:

- **Initialize**: if the workspace folder is not inside a git repository, the
  Source control view offers to create one there (`git init`, using your
  default branch name) and adds `.missive/` to the folder's `.gitignore`,
  creating the file if needed. Add a remote (see **Remotes** below) to push it.
- **Status** lists changed files inside the workspace folder only (the
  workspace can be a subfolder of a larger repository), plus the branch, its
  upstream and how many commits it is ahead of or behind it. It refreshes when
  you come back to the window. An upstream branch that was deleted on the
  remote is shown as such.
- **Fetch** (the cloud button) checks the remotes for new commits without
  changing your files (`git fetch --all --prune`), so the number of commits to
  pull is current.
- **Diff**: click a changed file to see what changed since the last commit.
  A request file can be shown as a **Request** — laid out like the request
  editor, read-only, with the changed name, method, URL, params, headers, auth
  fields, body and description marked — or as **Text**, its YAML side by side (or inline).
  Other files, and request files that can't be read as requests, are shown as
  text; binary files are not shown. Viewing a diff never stages anything.
- **Commit** stages and commits every change in the workspace, or only the files
  you tick in the changes list, and never touches files outside the workspace.
  Files in `.missive/` are never committed, even if they are not git-ignored.
  Git only sees saved files, so the view warns when open tabs have unsaved
  changes and offers **Save all**.
- **Identity**: if git has no name and email to record on commits
  (`user.name`, `user.email`), the view asks for them and saves them for all
  your repositories (`git config --global`) or, if you untick that, for this
  repository only.
- **Changes view**: the changes list can be shown as a flat list or as a
  folder tree.
- **Discard** (the undo button on a row, or *Discard selected* / *Discard all*)
  throws away uncommitted changes after a confirmation: edits and deletions go
  back to the last commit, and new files are deleted. It cannot be undone and is
  unavailable during a merge.
- **Pull** merges the upstream branch into yours; it never rebases. If the merge
  conflicts, nothing is resolved for you: the conflicted files are listed and
  Missive walks you through them (see below). A branch with no upstream yet
  pulls from the branch of the same name on `origin` (or the only remote) and
  tracks it from then on — so a repository you initialized in Missive can be
  joined with an existing remote one, even though their histories are
  unrelated. A pull that would overwrite uncommitted changes is refused and
  names the files to commit or discard first.
- **Push** pushes the current branch without force, to its upstream branch
  (even if it is named differently, or was deleted on the remote) unless you
  configured a separate push remote. A branch with no upstream yet is published
  to `origin` (or the only remote) and tracks it from then on; it needs at
  least one commit. If the remote has commits you don't have, the push is
  rejected, Missive fetches them so you can see what to pull, and asks you to
  pull first.
- **Errors** are explained in plain terms: failed sign-ins (with a hint to set
  up a credential helper or SSH key), an unreachable remote, a repository locked
  by another git program (Missive waits briefly and tries again first), or a
  repository git refuses to trust. Missive never shows a git prompt: git runs
  with terminal prompts disabled, though GUI credential helpers still work.
- **History**: the **History** tab lists the commits on the current branch that
  changed files in the workspace, newest first, 50 at a time (**Load more** for
  older ones). Choosing a commit opens it for review: its message, author and
  date, the workspace files it changed, and a diff of each file against the
  commit before it (the first parent, for a merge). A request file can be shown
  as a **Request** or as **Text**, just like in the diff view, and the same
  fallback to text applies. Reviewing history is
  read-only; it never checks out or changes anything.
- **Branches** lists the local branches (the current one marked, each with its
  upstream and last commit) and the remote branches as of the last fetch.
  - **Switch** checks out another local branch (`git switch`, never forced).
    It is refused while there are uncommitted changes in the workspace — the
    files are named, as for a pull — or a merge is in progress, so a switch
    never carries changes over or loses them. If open tabs have unsaved edits,
    Missive asks first: they stay in their tabs. After a switch the explorer,
    open tabs and environments are reloaded from disk.
  - **Create** (the + button) makes a branch at the current commit, without an
    upstream (**Publish** pushes it), and switches to it unless you untick
    that; uncommitted changes come along, since both branches start from the
    same commit.
  - **Check out** a remote branch (`origin/feature`) to create a local branch
    of the same name (`feature`) that tracks it, refused like a switch when
    there are uncommitted changes. A remote branch that a local branch
    already tracks offers to switch to that branch instead.
  - **Delete** removes a local branch after a confirmation; the current branch
    can't be deleted. If the branch has commits that aren't merged into its
    upstream or the current branch, git keeps it and Missive asks a second
    time, saying the commits will be lost, before deleting it anyway
    (`git branch -D`). Remote branches are never deleted.
- **Remotes** lists the repository's remotes with their URLs. You can add one,
  rename it or change its URL (`git remote add`, `rename`, `set-url`), or
  remove it after confirming (`git remote remove`, which also drops its
  remote-tracking branches; nothing is deleted on the remote). Nothing is
  fetched or pushed when you change remotes.

### Resolving merge conflicts

When a pull stops on conflicts, the Source control view shows a merge banner
and marks each conflicted file. **Resolve** opens it side by side, your
version (the current branch) next to the incoming one, and for each file you
choose:

- **Keep this version** — take your side or the incoming side as-is (or keep a
  deletion, if that side deleted the file);
- **Mark as resolved** — after editing the file yourself in any editor to
  combine both sides. Missive refuses while the file still has
  `<<<<<<<` / `=======` / `>>>>>>>` conflict markers.

Once every file is resolved, **Commit all** completes the merge; the commit
message is pre-filled with git's merge message. **Abort merge** abandons the
merge instead (`git merge --abort`) and puts your branch back as it was before
the pull, after asking you to confirm.

Missive reopens the last workspace at the next launch, if its folder still
exists. It also keeps the ten most recently opened workspaces, listed on the
welcome screen and in the header next to **Open folder**; folders that no longer
exist are dropped from the list. Both are stored per user, outside any
workspace, in `settings.json` in the app data folder (`%APPDATA%\Missive` on
Windows, `~/.config/Missive` on Linux and macOS).

## Architecture

```text
Angular UI (webview)  ⇄  Photino message bridge  ⇄  .NET backend
```

Requests are sent from the .NET backend rather than the webview, so they are not
subject to browser CORS rules and can use any headers.

The backend follows **clean architecture**. Dependencies point inward only:

```text
Missive.Desktop ──► Missive.Infrastructure ──► Missive.Application ──► Missive.Domain
```

(`Missive.Desktop` also references `Missive.Application` directly to invoke use
cases.)

| Project | Responsibility | May reference |
| --- | --- | --- |
| `Missive.Domain` | Entities and value objects (`Workspace`, `Collection`, `HttpRequestDefinition`, `Environment`, `Auth`), variable resolution and other pure rules. No I/O, no framework dependencies. | — |
| `Missive.Application` | Use cases (open workspace, save request, send request, resolve environment, commit/pull/push) and the ports they need, e.g. `IWorkspaceRepository`, `IHttpSender`, `IGitService`, `ISecretStore`, `IHistoryStore`. | Domain |
| `Missive.Infrastructure` | Adapters implementing the ports: YAML file storage, `HttpClient` sender, git, `.missive/` secrets, cookies and history. | Application, Domain |
| `Missive.Desktop` | Composition root and presentation adapter: Photino window, bridge message dispatch to use cases, dependency injection wiring. | All of the above |

The Angular client is a separate presentation layer that only talks to
`Missive.Desktop` through bridge messages.

## Prerequisites

- .NET 10 SDK
- Node.js 22.22.3+ (22.x) or 24.15.0+ (24.x), with npm
- git, for workspace synchronization
- Windows: Microsoft Edge WebView2 Runtime
- macOS: the system WebKit runtime (no additional install)
- Linux: GTK 3, WebKitGTK 4.1, and libnotify. On Ubuntu 24.04:
  `sudo apt-get install libgtk-3-0t64 libwebkit2gtk-4.1-0 libnotify4`

Linux needs a graphical desktop session. Build and publish for the operating
system/architecture on which the app will run.

## Run

From the repository root:

```sh
dotnet run --project src/Missive.Desktop
```

The .NET build runs `npm ci` and the Angular production build, then copies the
frontend into `wwwroot` beside the desktop executable. The host resolves assets
relative to its executable, not the current working directory, and serves them
to the webview through a custom scheme handler (`app://localhost/`, or an
intercepted `http://localhost/` on Windows) rather than `file://`, because
WebView2 won't run the Angular build's module scripts from `file://`.

## Frontend development

```sh
cd src/Missive.Client
npm ci
npm start
```

Open `http://localhost:4200` for Angular live reload in a browser. Outside the
desktop host there is no .NET backend, so the client talks to an in-memory
sample workspace instead (nothing is written to disk). The desktop host always
loads the bundled frontend; rebuild/restart it to see frontend changes. Routing uses hash URLs and assets use a relative base URL.

## Build and publish

```sh
dotnet build Missive.sln
dotnet publish src/Missive.Desktop -c Release -o artifacts/publish
```

Run the `Missive` executable (`Missive.exe` on Windows) in
`artifacts/publish`. Keep its `wwwroot` and `Assets` directories and native
libraries alongside it. This is a framework-dependent publish: the target machine needs the .NET 10
runtime and the platform webview prerequisites above, but not Node.js.

## Repository layout

- `src/Missive.Desktop`: Photino window, bridge, composition root, and
  build/publish integration.
- `src/Missive.Client`: standalone Angular app with SCSS styles, Tailwind
  CSS, Font Awesome icons, Angular CDK drag and drop and the Monaco editor.
- `src/Missive.Domain`, `src/Missive.Application`, `src/Missive.Infrastructure`:
  backend layers (see [Architecture](#architecture)).
- `tests/Missive.Domain.Tests`, `tests/Missive.Application.Tests`: fast unit
  tests with no I/O.
- `tests/Missive.Infrastructure.Tests`: integration tests against temporary
  folders and git repositories.

Run the backend tests (xUnit, with NSubstitute for mocking) with:

```sh
dotnet test Missive.sln
```

Run the client unit tests (Vitest, through `ng test`) from `src/Missive.Client`;
`npm test` watches for changes, and this runs them once:

```sh
npm test -- --watch=false
```

Format the client with Prettier (`npm run format`, or `npm run format:check`
to verify) from `src/Missive.Client`. No other linter is configured yet.

## CI and releases

GitHub Actions runs on every pull request and push to `main`
(`.github/workflows/ci.yml`): it checks client formatting, runs the client unit tests
and builds the Angular app, then builds the solution and runs the tests on Windows, Linux and macOS.

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org)
(`feat:`, `fix:`, `feat!:` / `BREAKING CHANGE:`, ...). After `npm install` at the
repo root, a [Lefthook](https://github.com/evilmartians/lefthook) `commit-msg`
hook runs commitlint (`commitlint.config.mjs`), and CI lints pull request
commits too. On every push to `main`, `.github/workflows/tag.yml` runs
[semantic-release](https://semantic-release.gitbook.io) (`.releaserc.json`),
which picks the next `vMAJOR.MINOR.PATCH` from the commits since the last tag
(`fix` → patch, `feat` → minor, breaking → major), pushes the tag and starts the
release build for it. Merge pull requests with a merge commit or by rebasing, not
by squashing: semantic-release reads every commit, and a squash would fold a
branch's `fix:` commits into one `feat:` and drop them from the release notes.

Pushing a version tag publishes desktop builds (`.github/workflows/release.yml`).
semantic-release does this automatically; to do it by hand:

```sh
git tag v0.1.0
git push origin v0.1.0
```

Each OS builds a self-contained publish (no .NET runtime needed on the target
machine) and packages it with [Velopack](https://velopack.io) (`vpk pack`) into
an installer, a portable copy and an update feed: `Missive-win-Setup.exe` and
`Missive-win-Portable.zip` for `win-x64`, `Missive.AppImage` for `linux-x64`,
and `Missive-osx-Setup.pkg` and `Missive-osx-Portable.zip` for `osx-arm64`.
The GitHub release for the tag gets only the installers (`Missive-win-Setup.exe`,
`Missive.AppImage`, `Missive-osx-Setup.pkg`), together with the `.nupkg`
packages and `releases.<channel>.json` feeds (channels `win`, `linux`, `osx`)
that installed copies read to update themselves; the portable copies are kept
in the workflow artifacts only. Tags with a suffix such as
`v0.2.0-beta.1` are marked as pre-releases; only pre-release builds update to
pre-releases. Running the workflow manually produces the same files as workflow
artifacts without creating a release. Keep the `vpk` version in the workflow
equal to the `Velopack` package version in `Missive.Infrastructure`. The builds
are not code-signed or notarized yet.
