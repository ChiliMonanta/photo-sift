# Setting up a Google OAuth client for PhotoSift

PhotoSift's Google Drive support needs an **OAuth client** (a client ID and client secret) from a Google Cloud project. The official builds use the maintainer's client, but those credentials are **not in this repository**. If you fork PhotoSift or build it yourself, create your own client by following this guide. It takes about 15 minutes and is free.

> **Status:** Google Drive support is being built (see [SPEC.md, section 8](../SPEC.md#8-google-drive-post-mvp-planned)). This guide prepares the credentials; the app starts using them when Drive sign-in is added.

Only the **developer** needs a Google Cloud project. People who use the app just sign in with their own Google account.

## 1. Create the project

1. Open <https://console.cloud.google.com> and sign in with the Google account that should own the app. A separate account for the project is a good idea, so it doesn't depend on your personal account.
2. Open the project menu at the top of the page → **New project**.
3. Name it, for example `PhotoSift`, and click **Create**. Make sure the new project is selected in the project menu before continuing.

## 2. Enable the Google Drive API

1. Go to **APIs & Services → Library**.
2. Search for **Google Drive API**, open it, and click **Enable**.

No billing account is needed.

## 3. Configure the consent screen

Open **Google Auth Platform** (in older versions of the console: **APIs & Services → OAuth consent screen**). The first time, click **Get started**.

1. **Branding**
   * **App name:** `PhotoSift` (or your fork's name; it is shown to users when they sign in).
   * **User support email** and **Developer contact email:** an address you read.
   * Skip the **logo** for now. Adding one requires an extra brand review.
2. **Audience**
   * **User type:** **External**.
   * **Publishing status:** leave it in **Testing**.
   * **Test users:** add the Google accounts that will sign in, starting with your own. In Testing mode, only these accounts (at most 100) can sign in.
3. **Data access** → **Add or remove scopes**
   * Add `https://www.googleapis.com/auth/drive` (shown as "See, edit, create, and delete all of your Google Drive files").
   * Click **Update**, then **Save**.

   PhotoSift uses this scope to read file names, folders, sizes, and MD5 checksums, to download the small preview images Drive generates, and to upload photos into folders you choose (**Upload to Google Drive**). It never downloads the photos themselves, and it never changes or deletes existing Drive files. The narrower `drive.file` scope would only let PhotoSift upload into folders it created itself, not into your existing folders.

   If you created the client with `drive.readonly` or `drive.metadata.readonly` earlier, add `drive` here as well. A `drive.readonly` sign-in keeps working for scanning; click **Allow uploads…** in the app to connect again with upload permission.

## 4. Create the OAuth client

1. Go to **Google Auth Platform → Clients → Create client**.
2. **Application type:** **Desktop app**. Name it, for example, `PhotoSift Desktop`.
3. Click **Create**, then **Download JSON**. The file is named like `client_secret_….apps.googleusercontent.com.json` and contains `client_id` and `client_secret`.

Keep the file **outside the repository**, for example in your Documents folder. This repository's `.gitignore` ignores `client_secret*.json` as a safety net, but don't rely on it.

> **Is the client secret really secret?** Not for desktop apps. Anyone can extract it from a released app, so OAuth treats desktop apps as *public clients* ([RFC 8252](https://datatracker.ietf.org/doc/html/rfc8252)). Security comes from the user's own consent in the browser and from PKCE. It still doesn't belong in a public repository: bots scan GitHub for credentials, and Google may disable a client whose secret is published.

## 5. Give the credentials to your local build

PhotoSift reads the credentials from .NET user secrets, which are stored in your user profile, not in the repository. From the repository root, using the values from the JSON file:

```sh
dotnet user-secrets set "GoogleDrive:ClientId" "<client_id>" --project src/PhotoSift.Desktop
dotnet user-secrets set "GoogleDrive:ClientSecret" "<client_secret>" --project src/PhotoSift.Desktop
```

Check them with:

```sh
dotnet user-secrets list --project src/PhotoSift.Desktop
```

User secrets are only used by Debug builds (`dotnet run`). Released apps get the credentials from the build, as described below.

You can also use environment variables, which work in every build and take precedence over the other sources. This is also how users of a released build can use their own client:

```sh
PHOTOSIFT_GOOGLE_CLIENT_ID=<client_id>
PHOTOSIFT_GOOGLE_CLIENT_SECRET=<client_secret>
```

## 6. Give the credentials to release builds (GitHub Actions)

The release workflow builds the credentials into the published app, but only for tag builds (releases). Manual and pull request builds, whose packages anyone can download, are built without them. In your repository on GitHub, go to **Settings → Secrets and variables → Actions → New repository secret** and add:

| Name | Value |
|---|---|
| `GOOGLE_DRIVE_CLIENT_ID` | `client_id` from the JSON file |
| `GOOGLE_DRIVE_CLIENT_SECRET` | `client_secret` from the JSON file |

Forks do not inherit these secrets. Without them, the app is built without a default Drive client and Drive sign-in is unavailable.

## 7. Testing mode and its limits

In **Testing** mode:

* Only the test users you listed can sign in (at most 100).
* Sign-ins expire after **7 days**; users then sign in again.
* Users see a notice that the app is not verified by Google.

That is fine for development and for a small group of people.

## 8. Making the app available to everyone (Google verification)

`drive` is a **restricted** scope, so an app that anyone can use must be **verified by Google**. When you are ready, go to **Audience → Publish app**, then start the review in **Verification Center**. Google asks for:

* a **home page** and a **privacy policy** on a domain you have verified ownership of (in Google Search Console);
* a **demo video** (unlisted YouTube video) that shows the sign-in, the consent screen, and how the app uses the data;
* a written **justification** for the scope: PhotoSift reads file metadata and MD5 checksums to find duplicate photos, downloads Drive's small preview images so the user can confirm duplicates visually, and uploads the user's photos into Drive folders the user chooses (which `drive.file` cannot do), skipping photos already there. Everything happens on the user's own device; no data is sent to any other server, and full photos are never downloaded.

Apps that access restricted-scope data through a server must also pass a yearly third-party security assessment (CASA). PhotoSift runs entirely on the user's computer, which normally means the assessment is not required, but Google decides during the review.

The review usually takes from a few weeks to a couple of months. If you add or broaden scopes later, the app needs a new review.

## Troubleshooting

| Problem | Cause and fix |
|---|---|
| **"Access blocked: PhotoSift has not completed the Google verification process"** | The account is not a test user. Add it under **Audience → Test users**. |
| **"Error 400: redirect_uri_mismatch"** | The client is not of type **Desktop app**. Create a new client with that type. |
| **"Google Drive API has not been used in project … or it is disabled"** | Enable the Drive API in the same project as the client (step 2). |
| **Sign-in worked last week but now fails** | In Testing mode, sign-ins expire after 7 days. Sign in again. |
| **Previews say "Google Drive refused the preview (HTTP 403)"** | The sign-in lacks a Drive scope. Check that `drive` is added under **Data access**, then disconnect and connect Google Drive again. |
| **"PhotoSift needs permission to see your Google Drive files and add new ones"** | The consent screen did not grant the `drive` scope. Check that it is added under **Data access**, then connect again and allow access. |
| **`dotnet user-secrets` says the project has no UserSecretsId** | Run the command from the repository root with `--project src/PhotoSift.Desktop`. |
