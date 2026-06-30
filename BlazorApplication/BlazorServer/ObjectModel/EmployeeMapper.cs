using AutoMapper;
using EmployeeManagement.Models;

namespace BlazorServer.ObjectModel
{
    public class EmployeeMapper :Profile
    {
        public EmployeeMapper()
        {
            CreateMap<Employee, EditEmployeeModel>()
                .ForMember(dest => dest.ConfirmEmail,
                           opt => opt.MapFrom(src => src.Email));
            CreateMap<EditEmployeeModel, Employee>();
        }
    }
}
