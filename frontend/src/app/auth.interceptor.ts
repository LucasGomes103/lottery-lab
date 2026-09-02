import { HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';

export const authInterceptor: HttpInterceptorFn = (request, next) => {
    const token = localStorage.getItem('lotteryLabToken');
    const authenticated = token ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request;
    return next(authenticated).pipe(catchError(error => {
        if (error.status === 401 && !request.url.endsWith('/auth/login') && !request.url.endsWith('/auth/me')) {
            localStorage.removeItem('lotteryLabToken');
            location.reload();
        }
        return throwError(() => error);
    }));
};
