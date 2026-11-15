# Release website

The lightweight static download page is in `website\`. It presents PhotoLibrarian
and its local-first behavior, and directs visitors to the Microsoft Store once
the public listing exists. Do not add a direct download for the unsigned CI
MSIX artifacts; they are Store submission packages and are not ordinary
installers. When the public listing is ready, replace the "Coming soon"
availability card in `website\index.html` with the official Store listing link.

## Azure hosting

The site is deployed with Azure Static Web Apps by
`.github\workflows\deploy-release-site.yml`. Create the Azure Static Web App
resource outside this repository, then add its deployment token as the
repository Actions secret `AZURE_STATIC_WEB_APPS_API_TOKEN`. Do not commit the
token or put it in workflow variables. The workflow deploys the `website`
directory on pushes to `master` when site files or this guide change, or when
manually started from the Actions tab. It runs the local static checks before
deploying.

No Azure resource or GitHub secret is configured by this change. The workflow
will stop with an explicit message until the deployment token is available.

## Page-view measurement

The page currently sends no analytics, cookies, identifiers, or other
instrumentation. No Azure Monitor / Application Insights configuration is
available to this repository. Add aggregate page-view measurement only after
the Azure resource is configured and its telemetry settings can be verified to
avoid cookies and persistent visitor identifiers. Until then, this is an
intentional integration point rather than a third-party tracking substitute.

## Local checks

On Windows with PowerShell 7 or Windows PowerShell, run:

```powershell
.\website\Test-ReleaseSite.ps1
```

The page is plain HTML and CSS and has no package install or build step.
