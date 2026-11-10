# Getting started

**MynaHub** is the office app and the suite hub for **OSAS** (Open Source Architecture Suit). Create or open the company file here, sign in with the admin password, and open CoreOp, Draft or Form from the **Other apps** buttons along the bottom. Those apps never ask for the company admin password.

## Create a company

Companies are created in the **Myna**, not in the MynaHub window.

1. Open Myna (the tray icon, or **Choose or create a company…** on MynaHub's File menu).
2. Choose **Create a new company…**.
3. Enter the company's name and an admin password of 12 or more characters, twice.
4. Choose a folder and file name. Myna adds `.osas` if you leave the extension off.

Myna then shows a recovery code **once**. Store it somewhere you can find later. It is the way back into the owner's account if the password is forgotten.

## Open a company and sign in

In Myna choose **Open company file…** (or **Open remembered company**), pick the `.osas` file (files from earlier versions end in `.adms` and open the same way), then sign in with your user name and password. MynaHub, started from Myna, opens on that company. The suite remembers which file you chose (`%LOCALAPPDATA%\OSAS\suite.json`) so other suite apps can follow it. It never stores a password there.

## Other apps (bottom bar)

After you are signed in, use the **Other apps** bar along the bottom. Each button starts CoreOp, Draft or Form with the company path and a one-time ticket (`OSAS_COMPANY` / `OSAS_TICKET`). The app opens already signed in as you when the ticket is still valid.

## People

Who can sign in, their roles and their passwords are managed in Studio, under **Office → Security and backup → People**. They are the same in every app. Your own password confirms each change.

**Forgot password? Use recovery code** on the sign-in page asks for the recovery code and a new password. The old recovery code stops working, and a new one is shown once.

## Several apps, one file

MynaHub and MynaOps can have the same company file open at the same time, and what one saves the other shows within a few seconds. A protected (encrypted) file can be open in one program at a time.

## What the file holds

The `.osas` file holds the records, issued PDFs, attachments, and audit history. Copy that file to move or archive the practice. MynaHub keeps a copy before it upgrades the file. A plain file is not encrypted, so keep the folder as private as the practice records inside it.

## Finding your way around

The **tabs along the top** are the main areas: Practice, Documents, Accounts, Office and Suite. The **second row of tabs** holds the pages under the open area, and the page opens straight below it. The **Theme** button at the right switches light and dark.

- The **assistant** is the glowing logo at the bottom of every page. It answers questions about the practice, as you, running on this computer: no key, no account, nothing leaves the computer. It only reads. The model is Tulu 3 8B (Allen Institute for AI), the only one, and it is used by MynaHub only. It runs with a runtime MynaHub carries; see `docs/AI_RUNTIME.md` for the files it needs. Press **Check for Tulu** to see whether it is installed.
- **Suite** opens MynaOps, MynaCAD and MynaMod signed in as you, and manages the company's users and the file monitor.
