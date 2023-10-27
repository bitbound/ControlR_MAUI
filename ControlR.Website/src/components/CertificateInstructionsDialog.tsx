import {
    Button,
    Link,
    Typography,
    Dialog,
    DialogTitle,
    DialogContent,
    DialogContentText,
    DialogActions
} from '@mui/material'

interface CertficiateDialogProps {
    isOpen: boolean;
    onClose: () => void;
}

function CertificateInstructionsDialog(props: CertficiateDialogProps) {
    return (
        <>
            <Dialog
                open={props.isOpen}
                onClose={props.onClose}
                aria-labelledby="certificate-dialog"
            >
                <DialogTitle id="certificate-dialog">
                    MSIX Code Signing Certificate
                </DialogTitle>
                <DialogContent>
                    <DialogContentText>
                        The MSIX installer is created with a self-signed certificate.
                        To install it, you must first install the certificate in
                        the "Local Machine - Trusted People" certificate store.
                    </DialogContentText>
                    <DialogContentText sx={{ mt: 1 }}>
                        You can find instructions in Microsoft's&nbsp;
                        <Link href="https://learn.microsoft.com/en-us/dotnet/maui/windows/deployment/publish-cli#installing-the-app" target="_blank">
                            offical documentation
                        </Link>.
                    </DialogContentText>
                </DialogContent>
                <DialogActions>
                    <Button onClick={props.onClose} autoFocus>
                        Close
                    </Button>
                </DialogActions>
            </Dialog>
        </>
    )
}

export default CertificateInstructionsDialog;