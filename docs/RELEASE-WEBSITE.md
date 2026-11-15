# Release website

The lightweight product/download page is `website\index.html`; the dedicated
privacy policy is `website\privacy.html`. Both are plain static pages with no
JavaScript, cookies, or analytics. The policy describes local photo processing,
optional model downloads, user-initiated maps/sharing, and web hosting. Review
its contact and service details before using its URL in the Store listing.

Do not add a direct download for the unsigned CI MSIX artifacts: they are Store
submission packages, not ordinary installers. Microsoft signs the package
distributed through Partner Center. The public Store listing is not available
yet, so the product page intentionally shows a "Coming soon" card.

## 1. Create Azure hosting and hand off its deployment token

The repository already has a GitHub Actions workflow,
`.github\workflows\deploy-release-site.yml`; it deploys `website\` to Azure
Static Web Apps. The Azure account that hosts model files can create this
separate Static Web App resource in the same subscription. Do not change the
model Blob Storage account or its public model container for this website.

1. Sign in to the [Azure portal](https://portal.azure.com), select the existing
   subscription, and create **Static Web App**.
2. Choose a resource group (an existing group is fine, or use a dedicated
   website group), a unique resource name, the **Free** hosting plan, and an
   available region. For deployment source, choose **Other**: this repository
   already contains its custom GitHub Actions workflow.
3. Wait until deployment finishes provisioning. From the resource's Overview,
   copy its default public hostname (`https://<name>.azurestaticapps.net`).
4. Open **Manage deployment token** for that Static Web App and copy its token.
   Treat it as a credential. Do not put it in a pull request, issue, email, or
   chat, and do not commit it.
5. In GitHub, open **dunclaw/PhotoLibrarian > Settings > Secrets and variables
   > Actions > New repository secret**. Set the name to exactly
   `AZURE_STATIC_WEB_APPS_API_TOKEN` and paste the token as its value. This
   requires repository permission to manage Actions secrets.
6. Hand off only the public `azurestaticapps.net` hostname and confirmation
   that the secret was saved. Never send the token itself. No Azure resource or
   secret is created by this repository change.

Pull requests run the site checks but do not deploy. Once the PR is merged, a
push to `master` that changes the website or this guide triggers deployment;
the workflow can also be started manually. Adding the secret does not deploy
anything by itself. Use the resulting public hostname for the Store privacy
policy URL until a custom domain is configured.

## 2. Hand off the Store listing when it is public

Do this later, after the Microsoft Store listing is approved and publicly
accessible:

1. Copy the official public listing URL from Partner Center / its public Store
   listing. Do not use a private flight, Partner Center URL, or guessed product
   URL.
2. Send the public URL to the website maintainer in the PR or a repository
   issue. The URL is public information; no credentials are needed.
3. Update the availability card in `website\index.html` to link to that exact
   Store listing, replacing the "Coming soon" text.
4. In Partner Center, set the app's privacy-policy URL to the deployed page:
   `https://<site-hostname>/privacy.html` (or the matching custom-domain URL).
   Verify the page is publicly reachable without sign-in before submitting or
   updating the listing.
5. Before submission, confirm the policy still matches the shipped app and
   provide a durable privacy contact method if the GitHub profile contact
   option is not sufficient. Recheck the page and listing links after
   publication.

## Page-view measurement

No page-view analytics are currently configured. The site does not send
tracking events or set tracking cookies. Azure hosting necessarily receives
ordinary page requests, but aggregate page-view measurement should be added
only after an Azure monitoring configuration is selected and verified not to
use cookies or persistent visitor identifiers. Update the privacy policy if
that changes.

## Local checks

On Windows with PowerShell 7 or Windows PowerShell, run:

```powershell
.\website\Test-ReleaseSite.ps1
```

The page is plain HTML and CSS and has no package install or build step.
