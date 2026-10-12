CREATE TRIGGER invoices_cancelled_no_update BEFORE UPDATE ON invoices
WHEN OLD.status = 'cancelled'
BEGIN SELECT RAISE(ABORT, 'cancelled invoices cannot be modified'); END;
CREATE TRIGGER invoices_cancelled_no_delete BEFORE DELETE ON invoices
WHEN OLD.status = 'cancelled'
BEGIN SELECT RAISE(ABORT, 'cancelled invoices cannot be deleted'); END;
