using BlazorServer.Services;
using EmployeeManagement.Components;
using EmployeeManagement.Models;
using Microsoft.AspNetCore.Components;
using System.Threading.Tasks;

namespace BlazorServer.Pages
{
    public partial class DisplayButtonEmployeeList
    {
        [Parameter]
        public Employee Employee { get; set; }

        [Parameter]
        public bool ShowFooter { get; set; }

        //select count 
        protected bool IsSelected { get; set; }

        [Parameter]
        public EventCallback<bool> OnEmployeeSelection { get; set; }
        [Parameter]
        public EventCallback<int> OnEmployeeDeleted { get; set; }

        [Inject]
        public IEmployeeService EmployeeService { get; set; }

        [Inject]
        public NavigationManager NavigationManager { get; set; }
        protected Confirm DeleteConfirmation { get; set; }
        
        protected async Task CheckBoxChanged(ChangeEventArgs e)
        {
            IsSelected = (bool)e.Value;
            await OnEmployeeSelection.InvokeAsync(IsSelected);
        }

        //if you want page refresh after delete
        /* protected async Task Delete_Click()
         {
             await EmployeeService.DeleteEmployee(Employee.EmployeeId);
            // await OnEmployeeDeleted.InvokeAsync(Employee.EmployeeId);
             NavigationManager.NavigateTo("/employeelist", true);
         }*/

    
        //if you want page not refresh smooth
        protected void Delete_Click()
        {
           
           DeleteConfirmation.Show();
        }

        protected async Task ConfirmDelete_Click(bool deleteConfirmed)
        {
            if (deleteConfirmed)
            {
                await EmployeeService.DeleteEmployee(Employee.EmployeeId);
                await OnEmployeeDeleted.InvokeAsync(Employee.EmployeeId);
            }
        }
    }
}
