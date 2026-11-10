# Bank statements

Open **Billing → Bank reconciliation**.

## Add the account

On **Accounts**, choose **New account**. Enter the account name, bank, account number, and IFSC, then **Create**. Select a row later to edit it and **Save changes**.

## Import a statement

Banks put Date, Particulars, Credit, and Debit in different orders. MynaHub asks you to map them for each file.

1. Open **Import statement**.
2. Choose the bank account.
3. Choose **Choose statement** and pick a `.csv` or `.xlsx` file. An older `.xls` workbook needs to be saved as `.xlsx` or `.csv` first.
4. MynaHub shows the columns in the file's own order, with a sample from the first rows.
5. Set **Date**, **Particulars**, **Credit**, **Debit**, and the date order (day-month-year, month-day-year, or year-month-day).
6. Choose **Check rows** and read the preview.
7. Choose **Import**.

A row with a blank date is left out. Particulars, credit, debit, and any other text on that row are omitted. A date that is present but cannot be read stays in the check as an error so you can fix the file or the date order.

Import keeps the original rows, flags duplicates, and does not mark an invoice paid by itself. Matching a bank line to a receipt is a separate step on **Reconciliation**.
